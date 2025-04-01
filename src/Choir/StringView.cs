using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Choir;

public readonly struct StringView(ReadOnlyMemory<char> memory, int hashCode)
    : IEnumerable<char>
    , IEnumerable
    , IComparable
    , IEquatable<StringView>
    , IEquatable<string?>
    , IComparable<StringView>
    , IComparable<string?>
{
    private const int NullSortOrderValue = 1;

    public static bool operator ==(StringView left, StringView right) => left.Equals(right);
    public static bool operator !=(StringView left, StringView right) => !(left == right);

    public static bool operator ==(StringView left, string right) => left.Equals(right);
    public static bool operator !=(StringView left, string right) => !(left == right);

    public static bool operator ==(string left, StringView right) => right.Equals(left);
    public static bool operator !=(string left, StringView right) => !(left == right);

    public static implicit operator StringView(string s) => new(s.AsMemory(), string.GetHashCode(s.AsMemory().Span));
    public static implicit operator StringView(ReadOnlyMemory<char> m) => new(m, string.GetHashCode(m.Span));

    public static implicit operator ReadOnlyMemory<char>(StringView sv) => sv.Memory;
    public static implicit operator ReadOnlySpan<char>(StringView sv) => sv.Span;

    public static explicit operator string(StringView sv) => sv.ToString();

    public readonly ReadOnlyMemory<char> Memory = memory;
    public readonly int HashCode = hashCode;

    public int Length => Memory.Length;
    public ReadOnlySpan<char> Span => Memory.Span;
    public char this[int index] => Memory.Span[index];
    public char this[Index index] => Memory.Span[index];
    public StringView this[Range range] => Memory[range];

    public override string ToString() => new(Span);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is StringView s && Equals(s);
    public bool Equals(StringView other) => Span.Equals(other.Span, StringComparison.Ordinal);
    public bool Equals(StringView other, StringComparison comparisonType) => Span.Equals(other.Span, comparisonType);
    public bool Equals(string? other) => other is not null && Span.Equals(other.AsSpan(), StringComparison.Ordinal);
    public bool Equals(string? other, StringComparison comparisonType) => other is not null && Span.Equals(other.AsSpan(), comparisonType);
    public override int GetHashCode() => HashCode;

    public int CompareTo(object? obj) => obj is StringView otherView ? CompareTo(otherView) : obj is string otherString ? CompareTo(otherString) : NullSortOrderValue;
    public int CompareTo(StringView other) => Span.CompareTo(other.Span, StringComparison.Ordinal);
    public int CompareTo(StringView other, StringComparison comparisonType) => Span.CompareTo(other.Span, comparisonType);
    public int CompareTo(string? other) => other is null ? NullSortOrderValue : Span.CompareTo(other.AsSpan(), StringComparison.Ordinal);
    public int CompareTo(string? other, StringComparison comparisonType) => other is null ? NullSortOrderValue : Span.CompareTo(other.AsSpan(), comparisonType);

    public IEnumerator<char> GetEnumerator() => new StringViewEnumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool StartsWith(string s) => StartsWith(s.AsSpan());
    public bool StartsWith(ReadOnlyMemory<char> m) => StartsWith(m.Span);
    public bool StartsWith(StringView s) => StartsWith(s.Span);
    public bool StartsWith(ReadOnlySpan<char> s)
    {
        if (Length < s.Length) return false;

        for (int i = 0; i < s.Length; i++)
        {
            if (Span[i] != s[i])
                return false;
        }
        
        return true;
    }

    public bool EndsWith(string s) => EndsWith(s.AsSpan());
    public bool EndsWith(ReadOnlyMemory<char> m) => EndsWith(m.Span);
    public bool EndsWith(StringView s) => EndsWith(s.Span);
    public bool EndsWith(ReadOnlySpan<char> s)
    {
        if (Length < s.Length) return false;

        int endOffset = Length - s.Length;
        for (int i = 0; i < s.Length; i++)
        {
            if (Span[endOffset + i] != s[i])
                return false;
        }

        return true;
    }

    private sealed class StringViewEnumerator(StringView sv)
        : IEnumerator<char>
    {
        private readonly StringView _value = sv;
        private int _index = 0;

        public char Current => _index < _value.Length ? _value[_index] : '\0';
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            _index++;
            return _index >= _value.Length;
        }

        public void Reset() => _index = 0;
        public void Dispose() { }
    }
}
