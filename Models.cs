using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace PS5MemoryPeeker;

public enum MemoryValueKind
{
    Byte,
    UInt16,
    UInt32,
    UInt64,
    Float,
    Double,
    Hex,
    Text,
    Pointer
}

public enum ScanCompareKind
{
    ExactValue,
    FuzzyValue,
    IncreasedValue,
    IncreasedBy,
    DecreasedValue,
    DecreasedBy,
    BiggerThan,
    SmallerThan,
    ChangedValue,
    UnchangedValue,
    BetweenValue,
    UnknownInitialValue
}

public sealed class ProcessItem
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string TitleId { get; set; } = "";
    public string Path { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string GameTitle { get; set; } = "";
    public bool IsGameProcess { get; init; }
    public int Rank { get; init; }
    [JsonIgnore]
    public string Display => "EBOOT HOOKED ✅";
}

public sealed class MemorySection
{
    public bool IsSelected { get; set; }
    public string Name { get; init; } = "";
    public int Index { get; set; }
    public string Kind { get; set; } = "Memory";
    public int SelectionScore { get; set; }
    public ulong Start { get; init; }
    public ulong End { get; init; }
    public uint Protection { get; init; }
    public ulong ByteLength => End > Start ? End - Start : 0;
    public int Length => checked((int)Math.Min((ulong)int.MaxValue, ByteLength));
    [JsonIgnore]
    public string DisplayName => $"{Name}[{Index}]-{Protection:X}-{Start:X}-{ByteLength / 1024}KB";
}

public sealed class ScanResultRow
{
    public bool IsActive { get; set; }
    public bool IsLocked { get; set; }
    public string Description { get; set; } = "";
    public ulong Address { get; init; }
    public string AddressText => $"0x{Address:X}";
    public ulong SectionStart { get; init; }
    public ulong SectionOffset => SectionStart > 0 && Address >= SectionStart ? Address - SectionStart : Address;
    public MemoryValueKind Type { get; init; }
    public string TypeText => MemoryValueCodec.ToDisplayName(Type);
    public string Value { get; set; } = "";
    public string Hex { get; set; } = "";
    public string Section { get; init; } = "";
    public byte[] Bytes { get; set; } = [];
}

public sealed class CheatRow
{
    public bool IsActive { get; set; } = true;
    public bool IsLocked { get; set; }
    public string Description { get; set; } = "";
    public ulong Address { get; set; }
    public ulong SectionStart { get; set; }
    public string OriginalHex { get; set; } = "";
    public ulong SectionOffset => SectionStart > 0 && Address >= SectionStart ? Address - SectionStart : Address;
    public string AddressText
    {
        get => $"0x{Address:X}";
        set => Address = MemoryValueCodec.ParseAddress(value);
    }
    public MemoryValueKind Type { get; set; } = MemoryValueKind.UInt32;
    public string TypeText
    {
        get => MemoryValueCodec.ToDisplayName(Type);
        set => Type = MemoryValueCodec.FromDisplayName(value);
    }
    public string Value { get; set; } = "0";
    public string Section { get; set; } = "";
}

public sealed class PeekerState
{
    public ObservableCollection<ProcessItem> Processes { get; } = [];
    public ObservableCollection<MemorySection> Sections { get; } = [];
    public BulkObservableCollection<ScanResultRow> Results { get; } = [];
    public ObservableCollection<CheatRow> Cheats { get; } = [];
}

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class ConnectionHistoryItem
{
    public string Host { get; set; } = "";
    public string Port { get; set; } = "";
    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Port) ? Host : $"{Host}:{Port}";
}
