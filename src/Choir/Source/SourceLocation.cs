using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Choir.Source;

public readonly struct SourceLocation(int offset)
    : IAdditiveIdentity<SourceLocation, SourceLocation>
    , IAdditionOperators<SourceLocation, SourceLocation, SourceLocation>
    , IAdditionOperators<SourceLocation, int, SourceLocation>
    , ISubtractionOperators<SourceLocation, SourceLocation, SourceLocation>
    , ISubtractionOperators<SourceLocation, int, SourceLocation>
    , IComparable<SourceLocation>
    , IEquatable<SourceLocation>
{
    public static SourceLocation AdditiveIdentity { get; } = new(0);

    public static implicit operator int(SourceLocation location) => location.Offset;
    public static implicit operator SourceLocation(int offset) => new(offset);

    public static bool operator ==(SourceLocation left, SourceLocation right) => left.Offset == right.Offset;
    public static bool operator !=(SourceLocation left, SourceLocation right) => left.Offset != right.Offset;

    public static SourceLocation operator +(SourceLocation left, SourceLocation right) => new(left.Offset + right.Offset);
    public static SourceLocation operator +(SourceLocation left, int right) => new(left.Offset + right);
    public static SourceLocation operator +(int left, SourceLocation right) => new(left + right.Offset);

    public static SourceLocation operator -(SourceLocation left, SourceLocation right) => new(left.Offset - right.Offset);
    public static SourceLocation operator -(SourceLocation left, int right) => new(left.Offset - right);
    public static SourceLocation operator -(int left, SourceLocation right) => new(left - right.Offset);

    public static SourceLocation Min(SourceLocation left, SourceLocation right) => left.Offset < right.Offset ? left : right;
    public static SourceLocation Max(SourceLocation left, SourceLocation right) => left.Offset > right.Offset ? left : right;

    public readonly int Offset = offset;

    public override string ToString() => $"{nameof(SourceLocation)}({Offset})";
    public override int GetHashCode() => HashCode.Combine(Offset);
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is SourceLocation other && Equals(other);
    public bool Equals(SourceLocation other) => Offset == other.Offset;
    public int CompareTo(SourceLocation other) => Offset.CompareTo(other.Offset);
}
