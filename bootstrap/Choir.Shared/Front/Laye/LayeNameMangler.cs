using System.Diagnostics;
using System.Text;

using Choir.Front.Laye.Sema;

namespace Choir.Front.Laye;

public sealed class LayeNameMangler(ChoirContext context, LayeModule module)
{
    public const string NamePrefix = "_L";
    public const string ModuleNameSpecifier = "M";

    private readonly Dictionary<SemaDeclNamed, string> _cache = [];

    public ChoirContext Context { get; } = context;
    public LayeModule Module { get; } = module;

    private string? _moduleNameMangledCached;
    private string ModuleNameMangled
    {
        get
        {
            return _moduleNameMangledCached ??= Module.ModuleName is LayeConstants.ProgramModuleName ? "" : $"{ModuleNameSpecifier}{MangleIdentifier(Module.ModuleName)}";
        }
    }

    public string GetMangledName(SemaDeclNamed declNamed)
    {
        if (!_cache.TryGetValue(declNamed, out string? mangledName))
            _cache[declNamed] = mangledName = GetMangledNameImpl();

        return mangledName;

        string GetMangledNameImpl()
        {
            if (declNamed.ForeignSymbolName is not null)
                return NormalizeIdentifier(declNamed.ForeignSymbolName);

            var builder = new StringBuilder();
            builder.Append(NamePrefix);
            builder.Append(ModuleNameMangled);

            builder.Append(MangleIdentifier(declNamed.Name));

            switch (declNamed)
            {
                default:
                {
                    Context.Unreachable($"Unhandled decl in name mangler: {declNamed.GetType().FullName}.");
                    throw new UnreachableException();
                }

                case SemaDeclFunction declFunction:
                {
                    // TODO(local): mangle variadic-ness
                    builder.Append('f');

                    MangleTypeInto(builder, declFunction.ReturnType);
                    if (declFunction.ParameterDecls.Count > 0)
                    {
                        builder.Append(declFunction.ParameterDecls.Count);
                        foreach (var declParam in declFunction.ParameterDecls)
                            MangleTypeInto(builder, declParam.ParamType);
                    }
                } break;

                case SemaDeclStruct declStruct:
                {
                } break;
            }

            return builder.ToString();
        }
    }

    private string NormalizeIdentifier(string identifier)
    {
        static bool IsValidNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.';
        bool isValid = identifier.All(IsValidNameCharacter);
        Context.Assert(isValid, $"Only ASCII identifiers are supported (got '{identifier}')");
        return identifier;
    }

    private string MangleIdentifier(string identifier)
    {
        identifier = NormalizeIdentifier(identifier);
        return $"{identifier.Length}_{identifier}";
    }

    private void MangleTypeInto(StringBuilder builder, SemaTypeQual typeQual)
    {
        if (typeQual.IsQualified)
        {
            builder.Append('Q');
            if (typeQual.IsMutable) builder.Append('m');
            builder.Append('_');
        }

        MangleTypeInto(builder, typeQual.Type);
    }

    private void MangleTypeInto(StringBuilder builder, SemaType type)
    {
        type = type.CanonicalType;

        switch (type)
        {
            default:
            {
                Context.Unreachable($"Unhandled type in name mangler: {type.GetType().FullName}.");
                throw new UnreachableException();
            }

            case SemaTypeBuiltIn typeBuiltIn:
            {
                switch (typeBuiltIn.Kind)
                {
                    default:
                    {
                        Context.Unreachable($"Unhandled built-in type kind in name mangler: {typeBuiltIn.Kind}.");
                        throw new UnreachableException();
                    }

                    case BuiltinTypeKind.Void: builder.Append('v'); break;
                    case BuiltinTypeKind.NoReturn: builder.Append('V'); break;
                    case BuiltinTypeKind.Bool: builder.Append('b'); break;
                    case BuiltinTypeKind.BoolSized: builder.Append('B').Append(typeBuiltIn.Size.Bits).Append('_'); break;
                    case BuiltinTypeKind.Int: builder.Append('i'); break;
                    case BuiltinTypeKind.IntSized: builder.Append('I').Append(typeBuiltIn.Size.Bits).Append('_'); break;
                    case BuiltinTypeKind.FloatSized:
                    {
                        switch (typeBuiltIn.Size.Bits)
                        {
                            default:
                            {
                                Context.Unreachable($"Invalid float bit width in name mangler: {typeBuiltIn.Size.Bits}.");
                                throw new UnreachableException();
                            }

                            case 32: builder.Append('f'); break;
                            case 64: builder.Append('d'); break;
                        }
                    } break;

                    case BuiltinTypeKind.FFIBool: builder.Append("Cb"); break;
                    case BuiltinTypeKind.FFIChar: builder.Append("Cc"); break;
                    case BuiltinTypeKind.FFIShort: builder.Append("Cs"); break;
                    case BuiltinTypeKind.FFIInt: builder.Append("Ci"); break;
                    case BuiltinTypeKind.FFILong: builder.Append("Cl"); break;
                    case BuiltinTypeKind.FFILongLong: builder.Append("CL"); break;
                    case BuiltinTypeKind.FFIFloat: builder.Append("Cf"); break;
                    case BuiltinTypeKind.FFIDouble: builder.Append("Cd"); break;
                    case BuiltinTypeKind.FFILongDouble: builder.Append("CD"); break;
                }
            } break;

            case SemaTypePointer typePointer:
            {
                builder.Append("Xp");
                MangleTypeInto(builder, typePointer.ElementType);
            } break;

            case SemaTypeBuffer typeBuffer:
            {
                builder.Append("Xb");
                MangleTypeInto(builder, typeBuffer.ElementType);
            } break;

            case SemaTypeStruct typeStruct:
            {
                string mangledStructName = GetMangledName(typeStruct.DeclStruct);
                builder.Append('U');
                builder.Append(mangledStructName.AsSpan(NamePrefix.Length));
            } break;
        }
    }
}
