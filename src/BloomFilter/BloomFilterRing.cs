using System.Runtime.InteropServices;

namespace BloomFilter
{
    /// <summary>
    /// Параметры кольцевого ротатора фильтров Блума.
    /// </summary>
    public sealed class BloomFilterRingOptions
    {
        /// <summary>
        /// Количество фильтров в кольце (n).
        /// </summary>
        public int FilterCount { get; init; } = 3;

        /// <summary>
        /// Ожидаемая ёмкость каждого фильтра: по ней рассчитывается размер битовой карты.
        /// </summary>
        public long ExpectedItems { get; init; } = 1_000_000;

        /// <summary>
        /// Доля ожидаемой ёмкости, при которой активный фильтр заменяется следующим в кольце.
        /// 0.9 означает переключение после 90% от <see cref="ExpectedItems"/>.
        /// </summary>
        public double FillPercent { get; init; } = 0.9;

        /// <summary>
        /// Допустимая вероятность ложного срабатывания (например, 0.001 для 0.1%).
        /// </summary>
        public double FalsePositiveRate { get; init; } = 0.001;
    }

    /// <summary>
    /// Фабрика кольцевых ротаторов фильтров Блума.
    /// </summary>
    public static class BloomFilterRingFactory
    {
        /// <summary>
        /// Создает кольцевой ротатор из <paramref name="filterCount"/> фильтров ёмкостью
        /// <paramref name="expectedItems"/>; ротация происходит после заполнения на
        /// <paramref name="fillPercent"/> от этой ёмкости.
        /// </summary>
        /// <param name="filterCount">Количество фильтров в кольце (n).</param>
        /// <param name="expectedItems">Ожидаемая ёмкость каждого фильтра.</param>
        /// <param name="falsePositiveRate">Допустимая вероятность ложного срабатывания.</param>
        /// <param name="fillPercent">Доля ёмкости, после которой происходит ротация (по умолчанию 0.9).</param>
        public static BloomFilterRing Create(
            int filterCount,
            long expectedItems,
            double falsePositiveRate = 0.001,
            double fillPercent = 0.9)
        {
            return Create(new BloomFilterRingOptions
            {
                FilterCount = filterCount,
                ExpectedItems = expectedItems,
                FalsePositiveRate = falsePositiveRate,
                FillPercent = fillPercent,
            });
        }

        /// <summary>
        /// Создает кольцевой ротатор по набору параметров.
        /// </summary>
        public static BloomFilterRing Create(BloomFilterRingOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (options.FilterCount < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.FilterCount,
                    "Количество фильтров в кольце должно быть не меньше двух.");
            }

            if (options.ExpectedItems <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.ExpectedItems,
                    "Ожидаемая ёмкость фильтра должна быть больше нуля.");
            }

            if (options.FillPercent is <= 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.FillPercent,
                    "Доля заполнения должна быть в диапазоне (0, 1].");
            }

            if (options.FalsePositiveRate is <= 0 or >= 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.FalsePositiveRate,
                    "Вероятность ложного срабатывания должна быть в диапазоне (0, 1).");
            }

            // Порог переключения считается здесь, а не передается снаружи.
            long rotationThreshold = Math.Max(1, (long)Math.Ceiling(options.ExpectedItems * options.FillPercent));

            return new BloomFilterRing(
                options.FilterCount,
                options.ExpectedItems,
                rotationThreshold,
                options.FalsePositiveRate);
        }
    }

    /// <summary>
    /// Кольцевой ротатор фильтров Блума без блокировок на пути записи и чтения.
    /// </summary>
    /// <remarks>
    /// Содержит n фильтров. Запись всегда идет в активный фильтр; когда он принимает
    /// порог уникальных значений (90% ёмкости по умолчанию), активным становится следующий
    /// фильтр кольца, а самый старый экземпляр очищается и переиспользуется.
    /// Проверка наличия выполняется по всем n фильтрам, поэтому окно «памяти» составляет
    /// до n * rotationThreshold значений.
    ///
    /// Состояние кольца хранится в неизменяемом снимке (массив фильтров + индекс активного), который
    /// публикуется победителем CAS. Ротацию выполняет ровно один поток; остальные просто продолжают писать.
    ///
    /// При ротации освободившийся фильтр очищается на месте и переиспользуется, поэтому новые
    /// экземпляры и битовые карты не выделяются. Плата: в момент очистки читатель, который ещё
    /// держит старый снимок, может получить ложное отрицание по самым старым значениям -
    /// они и так покидают окно.
    ///
    /// Если между записью битов и публикацией кольцо успело провернуться, писатель замечает это
    /// (снимок изменился) и повторяет операцию на актуальном снимке, поэтому значение не теряется.
    ///
    /// Плата за отказ от блокировки: при одновременной вставке одного и того же значения несколькими
    /// потоками счётчик уникальных может увеличиться более одного раза (проверка и вставка не атомарны).
    /// Как и любой фильтр Блума, счётчик в принципе приблизителен из-за ложных срабатываний.
    /// </remarks>
    public sealed class BloomFilterRing
    {
        private readonly int _filterCount;
        private readonly long _rotationThreshold;
        private readonly long _expectedItems;
        private readonly double _falsePositiveRate;

        private RingState _state;
        private long _totalUniqueCount;
        private long _rotationCount;

        internal BloomFilterRing(int filterCount, long expectedItems, long rotationThreshold, double falsePositiveRate)
        {
            _filterCount = filterCount;
            _rotationThreshold = rotationThreshold;
            _expectedItems = expectedItems;
            _falsePositiveRate = falsePositiveRate;
            _state = CreateState(activeIndex: 0);
        }

        /// <summary>
        /// Количество фильтров в кольце (n).
        /// </summary>
        public int FilterCount => _filterCount;

        /// <summary>
        /// Вычисленный порог уникальных значений, после которого происходит ротация.
        /// </summary>
        public long RotationThreshold => _rotationThreshold;

        /// <summary>
        /// Индекс активного фильтра в кольце.
        /// </summary>
        public int ActiveIndex => Volatile.Read(ref _state).ActiveIndex;

        /// <summary>
        /// Количество уникальных значений, принятых активным фильтром.
        /// </summary>
        public long ActiveUniqueCount
        {
            get
            {
                RingState state = Volatile.Read(ref _state);
                return Volatile.Read(ref state.ActiveCount);
            }
        }

        /// <summary>
        /// Общее количество уникальных значений, принятых ротатором с момента создания.
        /// </summary>
        public long TotalUniqueCount => Interlocked.Read(ref _totalUniqueCount);

        /// <summary>
        /// Сколько раз кольцо уже переключалось на следующий фильтр.
        /// </summary>
        public long RotationCount => Interlocked.Read(ref _rotationCount);

        /// <summary>
        /// Добавляет строку в активный фильтр, если ротатор ещё не знает это значение.
        /// Возвращает true, если значение встретилось впервые.
        /// </summary>
        public bool TryAdd(ReadOnlySpan<char> item)
        {
            return TryAdd(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Добавляет набор байт в активный фильтр, если ротатор ещё не знает это значение.
        /// Возвращает true, если значение встретилось впервые.
        /// </summary>
        public bool TryAdd(ReadOnlySpan<byte> data)
        {
            // При повторе пропускаем проверку наличия: значение уже было записано в предыдущей
            // попытке, поэтому второй раз его "уникальным" не считаем - счётчик увеличится один раз.
            bool retried = false;

            while (true)
            {
                RingState state = Volatile.Read(ref _state);

                if (!retried && state.MightContain(data))
                {
                    return false;
                }

                int active = state.ActiveIndex;
                state.Filters[active].Add(data);

                // Если за время записи кольцо провернулось, биты могли попасть в фильтр,
                // который уже выведен из окна. Повторяем операцию на актуальном снимке.
                if (!ReferenceEquals(Volatile.Read(ref _state), state))
                {
                    retried = true;
                    continue;
                }

                long count = Interlocked.Increment(ref state.ActiveCount);
                Interlocked.Increment(ref _totalUniqueCount);

                if (count >= _rotationThreshold)
                {
                    TryRotate(state);
                }

                return true;
            }
        }

        /// <summary>
        /// Добавляет строку в активный фильтр. Возвращаемое значение игнорируется.
        /// </summary>
        public void Add(ReadOnlySpan<char> item)
        {
            TryAdd(item);
        }

        /// <summary>
        /// Добавляет набор байт в активный фильтр. Возвращаемое значение игнорируется.
        /// </summary>
        public void Add(ReadOnlySpan<byte> data)
        {
            TryAdd(data);
        }

        /// <summary>
        /// Проверяет наличие строки хотя бы в одном фильтре кольца.
        /// False - элемента ТОЧНО нет. True - элемент, скорее всего, есть.
        /// </summary>
        public bool MightContain(ReadOnlySpan<char> item)
        {
            return MightContain(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Проверяет наличие набора байт хотя бы в одном фильтре кольца.
        /// False - элемента ТОЧНО нет. True - элемент, скорее всего, есть.
        /// </summary>
        public bool MightContain(ReadOnlySpan<byte> data)
        {
            return Volatile.Read(ref _state).MightContain(data);
        }

        /// <summary>
        /// Полностью очищает кольцо, публикуя новый пустой снимок.
        /// </summary>
        /// <remarks>
        /// Не блокирует писателей: значения, добавленные в момент вызова, могут быть потеряны.
        /// Вызывайте, когда запись остановлена.
        /// </remarks>
        public void Clear()
        {
            while (true)
            {
                RingState current = Volatile.Read(ref _state);
                RingState fresh = CreateState(activeIndex: 0);

                if (ReferenceEquals(Interlocked.CompareExchange(ref _state, fresh, current), current))
                {
                    break;
                }
            }

            Interlocked.Exchange(ref _totalUniqueCount, 0);
            Interlocked.Exchange(ref _rotationCount, 0);
        }

        private RingState CreateState(int activeIndex)
        {
            var filters = new ConcurrentBloomFilter[_filterCount];
            for (int i = 0; i < filters.Length; i++)
            {
                filters[i] = new ConcurrentBloomFilter(_expectedItems, _falsePositiveRate);
            }

            return new RingState(filters, activeIndex);
        }

        private void TryRotate(RingState state)
        {
            // Для каждого снимка ротацию запускает только один поток-победитель.
            if (Interlocked.CompareExchange(ref state.RotationStarted, 1, 0) != 0)
            {
                return;
            }

            int next = (state.ActiveIndex + 1) % state.Filters.Length;

            // Освободившийся фильтр очищаем на месте и переиспользуем: новые битовые карты не выделяются.
            // В старом снимке в него уже никто не пишет - писатели всегда идут в ActiveIndex.
            state.Filters[next].Reset();

            // Массив ссылок не меняется, поэтому снимки могут делить его между собой.
            // Меняется только индекс активного фильтра.
            // CAS, а не Volatile.Write: если параллельный Clear() уже опубликовал новый снимок,
            // ротация не должна перетирать его своим устаревшим состоянием.
            var rotated = new RingState(state.Filters, next);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, rotated, state), state))
            {
                Interlocked.Increment(ref _rotationCount);
            }
        }

        private sealed class RingState
        {
            public readonly ConcurrentBloomFilter[] Filters;
            public readonly int ActiveIndex;

            /// <summary>Счётчик уникальных значений активного фильтра; нужен только пока снимок актуален.</summary>
            public long ActiveCount;

            /// <summary>Флаг «ротация этого снимка уже запущена» (0/1).</summary>
            public int RotationStarted;

            public RingState(ConcurrentBloomFilter[] filters, int activeIndex)
            {
                Filters = filters;
                ActiveIndex = activeIndex;
            }

            public bool MightContain(ReadOnlySpan<byte> data)
            {
                // Идём от активного фильтра к самым старым: недавно добавленные значения
                // находятся с первой проверки, а редкие промахи всё равно проверяют все n.
                int index = ActiveIndex;

                for (int i = 0; i < Filters.Length; i++)
                {
                    if (Filters[index].MightContain(data))
                    {
                        return true;
                    }

                    index = index == 0 ? Filters.Length - 1 : index - 1;
                }

                return false;
            }
        }
    }
}