namespace PS5MemoryPeeker;

public sealed class ScanEngine
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int MaxDisplayedResults = 200_000;
    private const ulong MaxBatchGap = 64 * 1024;
    private readonly IConsoleDebugClient _client;
    private List<ScanResultRow> _lastResults = [];

    public ScanEngine(IConsoleDebugClient client)
    {
        _client = client;
    }

    public bool HasPreviousScan => _lastResults.Count > 0;

    public void Clear()
    {
        _lastResults.Clear();
    }

    public async Task<IReadOnlyList<ScanResultRow>> FirstScanAsync(
        int pid,
        IReadOnlyList<MemorySection> sections,
        MemoryValueKind valueKind,
        ScanCompareKind compareKind,
        string firstValue,
        string secondValue,
        bool aligned,
        IProgress<(double Progress, string Message)> progress,
        CancellationToken cancellationToken)
    {
        byte[] firstNeedle = compareKind == ScanCompareKind.UnknownInitialValue ? [] : MemoryValueCodec.ToBytes(valueKind, firstValue);
        byte[] secondNeedle = compareKind == ScanCompareKind.BetweenValue ? MemoryValueCodec.ToBytes(valueKind, secondValue) : [];
        int valueSize = MemoryValueCodec.GetSize(valueKind, firstValue);
        int step = aligned ? Math.Max(1, valueSize) : 1;
        ulong totalBytes = 0;
        foreach (MemorySection section in sections)
        {
            totalBytes += section.ByteLength;
        }

        ulong processed = 0;
        List<ScanResultRow> results = [];

        foreach (MemorySection section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong offset = 0;
            while (offset < section.ByteLength)
            {
                ulong remaining = section.ByteLength - offset;
                int readLength = (int)Math.Min((ulong)(ChunkSize + valueSize), remaining);
                byte[] buffer = await _client.ReadMemoryAsync(pid, section.Start + offset, readLength, cancellationToken).ConfigureAwait(false);
                await Task.Run(
                    () => ScanBuffer(section, offset, buffer, valueKind, compareKind, firstNeedle, secondNeedle, valueSize, step, results),
                    cancellationToken).ConfigureAwait(false);

                offset += ChunkSize;
                processed += Math.Min((ulong)ChunkSize, remaining);
                progress.Report((SafeProgress(processed, totalBytes), $"Scanning {section.Kind}: {section.Name}"));

                if (results.Count >= MaxDisplayedResults)
                {
                    _lastResults = results;
                    progress.Report((1, $"Result cap reached ({MaxDisplayedResults:N0}). Narrow the scan for more precision."));
                    return results;
                }
            }
        }

        _lastResults = results;
        progress.Report((1, "Process Memory has been scanned."));
        return results;
    }

    public async Task<IReadOnlyList<ScanResultRow>> NextScanAsync(
        int pid,
        MemoryValueKind valueKind,
        ScanCompareKind compareKind,
        string firstValue,
        string secondValue,
        IProgress<(double Progress, string Message)> progress,
        CancellationToken cancellationToken)
    {
        byte[] firstNeedle = MemoryValueCodec.ToBytes(valueKind, firstValue);
        byte[] secondNeedle = compareKind == ScanCompareKind.BetweenValue ? MemoryValueCodec.ToBytes(valueKind, secondValue) : [];
        int valueSize = MemoryValueCodec.GetSize(valueKind, firstValue);
        List<ScanResultRow> results = [];
        int processed = 0;
        List<List<ScanResultRow>> batches = BuildAddressBatches(_lastResults.OrderBy(r => r.Address).ToList(), valueSize);

        await Parallel.ForEachAsync(batches, new ParallelOptions
        {
            MaxDegreeOfParallelism = 1,
            CancellationToken = cancellationToken
        }, async (batch, token) =>
        {
            List<ScanResultRow> localMatches = [];
            try
            {
                ulong start = batch[0].Address;
                ulong end = batch.Max(r => r.Address + (ulong)valueSize);
                int length = checked((int)(end - start));
                byte[] window = await _client.ReadMemoryAsync(pid, start, length, token).ConfigureAwait(false);

                await Task.Run(() =>
                {
                    foreach (ScanResultRow row in batch)
                    {
                        int index = checked((int)(row.Address - start));
                        byte[] current = window.AsSpan(index, valueSize).ToArray();
                        AddNextMatch(localMatches, row, valueKind, compareKind, firstNeedle, secondNeedle, current);
                    }
                }, token).ConfigureAwait(false);
            }
            catch
            {
                foreach (ScanResultRow row in batch)
                {
                    byte[] current = await _client.ReadMemoryAsync(pid, row.Address, valueSize, token).ConfigureAwait(false);
                    AddNextMatch(localMatches, row, valueKind, compareKind, firstNeedle, secondNeedle, current);
                }
            }

            lock (results)
            {
                results.AddRange(localMatches);
            }

            int done = Interlocked.Add(ref processed, batch.Count);
            if (done % 500 == 0 || done >= _lastResults.Count)
            {
                progress.Report((SafeProgress((ulong)done, (ulong)_lastResults.Count), "Process Memory | Analyzing..."));
            }
        });

        _lastResults = results.OrderBy(r => r.Address).Take(MaxDisplayedResults).ToList();
        progress.Report((1, "Next scan completed."));
        return _lastResults;
    }

    private static void ScanBuffer(
        MemorySection section,
        ulong sectionOffset,
        byte[] buffer,
        MemoryValueKind valueKind,
        ScanCompareKind compareKind,
        byte[] firstNeedle,
        byte[] secondNeedle,
        int valueSize,
        int step,
        List<ScanResultRow> results)
    {
        for (int i = 0; i <= buffer.Length - valueSize; i += step)
        {
            ReadOnlySpan<byte> current = buffer.AsSpan(i, valueSize);
            if (!MemoryValueCodec.Matches(valueKind, compareKind, firstNeedle, secondNeedle, [], current))
            {
                continue;
            }

            byte[] bytes = current.ToArray();
            results.Add(new ScanResultRow
            {
                Address = section.Start + sectionOffset + (ulong)i,
                SectionStart = section.Start,
                Type = valueKind,
                Value = MemoryValueCodec.ToDisplay(valueKind, bytes),
                Hex = MemoryValueCodec.ToHex(bytes),
                Section = section.Name,
                Bytes = bytes
            });

            if (results.Count >= MaxDisplayedResults)
            {
                return;
            }
        }
    }

    private static List<List<ScanResultRow>> BuildAddressBatches(IReadOnlyList<ScanResultRow> rows, int valueSize)
    {
        List<List<ScanResultRow>> batches = [];
        List<ScanResultRow> current = [];
        ulong currentStart = 0;
        ulong currentEnd = 0;

        foreach (ScanResultRow row in rows)
        {
            ulong rowEnd = row.Address + (ulong)valueSize;
            ulong gap = current.Count > 0 && row.Address > currentEnd ? row.Address - currentEnd : 0;
            bool canJoin = current.Count > 0
                && gap <= MaxBatchGap
                && rowEnd - currentStart <= ChunkSize;

            if (!canJoin)
            {
                if (current.Count > 0)
                {
                    batches.Add(current);
                }

                current = [row];
                currentStart = row.Address;
                currentEnd = rowEnd;
                continue;
            }

            current.Add(row);
            currentEnd = Math.Max(currentEnd, rowEnd);
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private static void AddNextMatch(
        List<ScanResultRow> results,
        ScanResultRow row,
        MemoryValueKind valueKind,
        ScanCompareKind compareKind,
        byte[] firstNeedle,
        byte[] secondNeedle,
        byte[] current)
    {
        if (!MemoryValueCodec.Matches(valueKind, compareKind, firstNeedle, secondNeedle, row.Bytes, current))
        {
            return;
        }

        results.Add(new ScanResultRow
        {
            Address = row.Address,
            SectionStart = row.SectionStart,
            Type = valueKind,
            Value = MemoryValueCodec.ToDisplay(valueKind, current),
            Hex = MemoryValueCodec.ToHex(current),
            Section = row.Section,
            Bytes = current
        });
    }

    private static double SafeProgress(ulong processed, ulong total)
    {
        if (total == 0)
        {
            return 0;
        }

        return Math.Clamp((double)processed / total, 0, 1);
    }
}
