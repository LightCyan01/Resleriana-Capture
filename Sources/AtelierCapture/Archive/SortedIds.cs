using System.Text;

namespace AtelierCapture.Archive;

internal readonly record struct IdRow(byte Category, string Id, byte Phase, string Signature);

internal sealed class SortedIds : IDisposable
{
    private const int ChunkRows = 4096;
    private const int FanIn = 16;
    private readonly string folder;
    private readonly List<IdRow> buffer = new(ChunkRows);
    private readonly List<string> files = new();
    private readonly Dictionary<string, int> levels = new(StringComparer.Ordinal);
    private long serial;
    private static readonly Comparer<IdRow> Order = Comparer<IdRow>.Create(
        (left, right) =>
        {
            var category = left.Category.CompareTo(right.Category);
            if (category != 0)
                return category;
            var id = StringComparer.Ordinal.Compare(left.Id, right.Id);
            if (id != 0)
                return id;
            var phase = left.Phase.CompareTo(right.Phase);
            return phase != 0 || left.Category is not (6 or 7)
                ? phase
                : StringComparer.Ordinal.Compare(left.Signature, right.Signature);
        }
    );

    internal SortedIds(string ownerFolder)
    {
        folder = Path.Combine(ownerFolder, ".archive-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
    }

    internal void Add(IdRow row)
    {
        if (row.Id.Length > 8192 || row.Signature.Length > 32768)
            throw new InvalidDataException(
                "Capture identity ledger row exceeds its control bound."
            );
        buffer.Add(row);
        if (buffer.Count == ChunkRows)
            Flush();
    }

    internal IEnumerable<IdRow> Read()
    {
        Flush();
        while (files.Count > FanIn)
        {
            var next = new List<string>();
            foreach (var group in files.Chunk(FanIn))
            {
                var merged = NewPath();
                using (var writer = OpenWriter(merged))
                    foreach (var row in Merge(group))
                        Write(writer, row);
                next.Add(merged);
                foreach (var input in group)
                    File.Delete(input);
            }
            files.Clear();
            files.AddRange(next);
        }
        return Merge(files.ToArray());
    }

    private void Flush()
    {
        if (buffer.Count == 0)
            return;
        buffer.Sort(Order);
        var path = NewPath();
        using (var writer = OpenWriter(path))
            foreach (var row in buffer)
                Write(writer, row);
        files.Add(path);
        levels.Add(path, 0);
        buffer.Clear();
        var level = 0;
        while (files.Count(input => levels[input] == level) >= FanIn)
        {
            var group = files.Where(input => levels[input] == level).Take(FanIn).ToArray();
            var merged = NewPath();
            using (var writer = OpenWriter(merged))
                foreach (var row in Merge(group))
                    Write(writer, row);
            foreach (var input in group)
            {
                File.Delete(input);
                files.Remove(input);
                levels.Remove(input);
            }
            files.Add(merged);
            levels.Add(merged, ++level);
        }
    }

    private string NewPath() =>
        Path.Combine(
            folder,
            (serial = checked(serial + 1)).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ) + ".ids"
        );

    private static BinaryWriter OpenWriter(string path) =>
        new(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536),
            Encoding.UTF8
        );

    private static void Write(BinaryWriter output, IdRow row)
    {
        output.Write(row.Category);
        output.Write(row.Id);
        output.Write(row.Phase);
        output.Write(row.Signature);
    }

    private static IEnumerable<IdRow> Merge(string[] paths)
    {
        var inputs = paths
            .Select(path => new BinaryReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536),
                Encoding.UTF8
            ))
            .ToArray();
        try
        {
            var queue = new PriorityQueue<(int Index, IdRow Row), IdRow>(Order);
            for (var i = 0; i < inputs.Length; i++)
                Next(i);
            while (queue.TryDequeue(out var item, out _))
            {
                yield return item.Row;
                Next(item.Index);
            }
            void Next(int index)
            {
                var input = inputs[index];
                if (input.BaseStream.Position == input.BaseStream.Length)
                    return;
                var row = new IdRow(
                    input.ReadByte(),
                    input.ReadString(),
                    input.ReadByte(),
                    input.ReadString()
                );
                queue.Enqueue((index, row), row);
            }
        }
        finally
        {
            foreach (var input in inputs)
                input.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(folder))
            File.Delete(path);
        Directory.Delete(folder);
    }
}
