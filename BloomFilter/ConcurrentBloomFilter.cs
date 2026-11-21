using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BloomFilter
{
    public sealed class ConcurrentBloomFilter
    {
        private readonly ulong[] _bits;
        private readonly ulong _bitCount;
        private readonly int _hashCount;

        /// <summary>
        /// Создает потокобезопасный фильтр Блума.
        /// </summary>
        /// <param name="expectedItems">Ожидаемое количество уникальных элементов (например, 1_500_000 с запасом).</param>
        /// <param name="falsePositiveRate">Допустимая вероятность ложного срабатывания (например, 0.001 для 0.1%).</param>
        public ConcurrentBloomFilter(long expectedItems, double falsePositiveRate = 0.001)
        {
            if (expectedItems <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedItems));
            if (falsePositiveRate is <= 0 or >= 1)
                throw new ArgumentOutOfRangeException(nameof(falsePositiveRate));

            // Оптимальное количество бит: m = -(n * ln(p)) / (ln(2)^2)
            double m = -(expectedItems * Math.Log(falsePositiveRate)) / (Math.Log(2) * Math.Log(2));

            // Округляем вверх до кратности 64, чтобы идеально лечь в ulong[]
            long ulongCount = (long)Math.Ceiling(m / 64.0);
            _bits = new ulong[ulongCount];
            _bitCount = (ulong)ulongCount * 64;

            // Оптимальное количество хеш-функций: k = (m / n) * ln(2)
            _hashCount = Math.Max(1, (int)Math.Round((_bitCount / (double)expectedItems) * Math.Log(2)));
        }

        // Приватный конструктор для восстановления из потока/файла
        private ConcurrentBloomFilter(ulong[] bits, int hashCount)
        {
            _bits = bits;
            _bitCount = (ulong)bits.Length * 64;
            _hashCount = hashCount;
        }

        /// <summary>
        /// Размер выделенной памяти в байтах.
        /// </summary>
        public long SizeInBytes => (long)_bits.Length * sizeof(ulong);

        /// <summary>
        /// Очищает битовую карту, сохраняя её размер и количество хеш-функций.
        /// </summary>
        /// <remarks>
        /// Не потокобезопасен относительно параллельного чтения: на время очистки возможны
        /// ложные отрицания. В кольцевом ротаторе вызывается только для фильтра, который
        /// покидает окно, поэтому его данные и так устарели.
        /// </remarks>
        public void Reset()
        {
            Array.Clear(_bits);
        }

        /// <summary>
        /// Добавляет строку (URL) в фильтр без выделения памяти в куче.
        /// </summary>
        public void Add(ReadOnlySpan<char> item)
        {
            Add(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Добавляет произвольный набор байт в фильтр.
        /// </summary>
        public void Add(ReadOnlySpan<byte> data)
        {
            var (h1, h2) = ComputeHashes(data);

            for (int i = 0; i < _hashCount; i++)
            {
                ulong bitIndex = (h1 + (ulong)i * h2) % _bitCount;
                int arrayIndex = (int)(bitIndex >> 6); // Деление на 64
                ulong mask = 1UL << (int)(bitIndex & 63); // Остаток от деления на 64

                // Быстрая проверка перед атомарной записью снижает нагрузку на кэш процессора (cache line bouncing)
                if ((Volatile.Read(ref _bits[arrayIndex]) & mask) == 0)
                {
                    Interlocked.Or(ref _bits[arrayIndex], mask);
                }
            }
        }

        /// <summary>
        /// Проверяет наличие строки (URL).
        /// False - элемента ТОЧНО нет. True - элемент, скорее всего, есть.
        /// </summary>
        public bool MightContain(ReadOnlySpan<char> item)
        {
            return MightContain(MemoryMarshal.AsBytes(item));
        }

        /// <summary>
        /// Проверяет наличие набора байт.
        /// </summary>
        public bool MightContain(ReadOnlySpan<byte> data)
        {
            var (h1, h2) = ComputeHashes(data);

            for (int i = 0; i < _hashCount; i++)
            {
                ulong bitIndex = (h1 + (ulong)i * h2) % _bitCount;
                int arrayIndex = (int)(bitIndex >> 6);
                ulong mask = 1UL << (int)(bitIndex & 63);

                if ((Volatile.Read(ref _bits[arrayIndex]) & mask) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Комбинированный метод: проверяет наличие и сразу добавляет, если элемента не было.
        /// Возвращает true, если элемент был добавлен впервые (не встречался ранее).
        /// </summary>
        public bool TryAdd(ReadOnlySpan<char> item)
        {
            return TryAdd(MemoryMarshal.AsBytes(item));
        }

        public bool TryAdd(ReadOnlySpan<byte> data)
        {
            var (h1, h2) = ComputeHashes(data);
            bool isNew = false;

            for (int i = 0; i < _hashCount; i++)
            {
                ulong bitIndex = (h1 + (ulong)i * h2) % _bitCount;
                int arrayIndex = (int)(bitIndex >> 6);
                ulong mask = 1UL << (int)(bitIndex & 63);

                if ((Volatile.Read(ref _bits[arrayIndex]) & mask) == 0)
                {
                    ulong previous = Interlocked.Or(ref _bits[arrayIndex], mask);
                    if ((previous & mask) == 0)
                    {
                        isNew = true;
                    }
                }
            }

            return isNew;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (ulong h1, ulong h2) ComputeHashes(ReadOnlySpan<byte> data)
        {
            // Получаем сразу 128 бит (два независимых 64-битных хеша) за один проход
            UInt128 hash = XxHash128.HashToUInt128(data);

            // Шаг двойного хеширования делаем нечётным. Размер битовой карты кратен 64,
            // поэтому нечётный шаг гарантирует орбиту длиной не меньше 64 позиций:
            // k проверок не могут схлопнуться в пару позиций даже на маленьких фильтрах.
            return ((ulong)hash, (ulong)(hash >> 64) | 1UL);
        }

        /// <summary>
        /// Сохраняет состояние фильтра в поток (например, в файл перед перезапуском).
        /// </summary>
        public void SaveTo(Stream stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(_hashCount);
            writer.Write(_bits.Length);

            // Записываем весь массив напрямую как сырые байты без лишних копирований
            ReadOnlySpan<byte> rawBytes = MemoryMarshal.AsBytes(_bits.AsSpan());
            stream.Write(rawBytes);
        }

        /// <summary>
        /// Загружает фильтр из потока.
        /// </summary>
        public static ConcurrentBloomFilter LoadFrom(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int hashCount = reader.ReadInt32();
            int length = reader.ReadInt32();

            var bits = new ulong[length];
            Span<byte> rawBytes = MemoryMarshal.AsBytes(bits.AsSpan());
            stream.ReadExactly(rawBytes);

            return new ConcurrentBloomFilter(bits, hashCount);
        }
    }
}
