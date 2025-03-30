using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Choir.Front.Laye.Sema;

public sealed class SemaDeclTemplateParameters(IReadOnlyList<SemaDeclTemplateParameter> templateParams)
    : BaseSemaNode
{
    public IReadOnlyList<SemaDeclTemplateParameter> TemplateParams { get; } = templateParams;
    public override IEnumerable<BaseSemaNode> Children { get; } = templateParams;
}

public abstract class SemaDeclTemplateParameter(Location location, string name) : SemaDeclNamed(location, name);

public sealed class SemaDeclTemplateTypeParameter(Location location, string name, bool isDuckTyped)
    : SemaDeclTemplateParameter(location, name)
{
    public bool IsDuckTyped { get; } = isDuckTyped;
    // TODO(local): constraints/contracts/concepts/whatever
}

public sealed class SemaDeclTemplateValueParameter(Location location, string name, SemaTypeQual paramType)
    : SemaDeclTemplateParameter(location, name)
{
    public SemaTypeQual ParamType { get; } = paramType;
    // TODO(local): constraints/contracts/concepts/whatever
    
    public override IEnumerable<BaseSemaNode> Children { get; } = [paramType];
}

public sealed class SemaDeclParam(Location location, string name, bool isRefParam, SemaTypeQual paramType)
    : SemaDeclNamed(location, name)
{
    public bool IsRefParam { get; } = isRefParam;
    public SemaTypeQual ParamType { get; } = paramType;
    // TODO(local): evaluated constant for default values, or even arbitrary semantic expressions depending
    
    public override IEnumerable<BaseSemaNode> Children { get; } = [paramType];
}

public sealed class SemaDeclFunction(Location location, string name)
    : SemaDeclNamed(location, name)
{
    public required Scope Scope { get; init; }

    public SemaTypeQual ReturnType { get; set; } = SemaTypePoison.Instance.Qualified(Location.Nowhere);
    public IReadOnlyList<SemaDeclParam> ParameterDecls { get; set; } = [];
    public SemaDeclTemplateParameters? TemplateParameters { get; set; }
    public SemaStmt? Body { get; set; }

    public CallingConvention CallingConvention { get; set; } = CallingConvention.Laye;
    public VarargsKind VarargsKind { get; set; } = VarargsKind.None;
    public bool IsInline { get; set; } = false;
    public bool IsDiscardable { get; set; } = false;

    public override IEnumerable<BaseSemaNode> Children
    {
        get
        {
            yield return ReturnType;
            foreach (var param in ParameterDecls)
                yield return param;
            if (TemplateParameters is not null)
                yield return TemplateParameters;
            if (Body is not null)
                yield return Body;
        }
    }

    public SemaTypeFunction FunctionType(ChoirContext context) => new(context, ReturnType, ParameterDecls)
    {
        CallingConvention = CallingConvention,
        IsDiscardable = IsDiscardable,
        VarargsKind = VarargsKind,
    };

    public override SerializedDeclKind SerializedDeclKind { get; } = SerializedDeclKind.Function;
    public override void Serialize(ModuleSerializer serializer, BinaryWriter writer)
    {
        #region Flags

        byte flags1 = 0;
        byte flags2 = 0;

        switch (CallingConvention)
        {
            default:
            {
                serializer.Context.Unreachable($"Unhandled calling convention in serializer: {CallingConvention}.");
                throw new UnreachableException();
            }

            case CallingConvention.CDecl: flags1 |= SerializerConstants.Attrib1CallingConventionCDecl; break;
            case CallingConvention.Laye: flags1 |= SerializerConstants.Attrib1CallingConventionLaye; break;
            case CallingConvention.StdCall: flags1 |= SerializerConstants.Attrib1CallingConventionStdCall; break;
            case CallingConvention.FastCall: flags1 |= SerializerConstants.Attrib1CallingConventionFastCall; break;
        }

        if (IsForeign) flags1 |= SerializerConstants.Attrib1ForeignFlag;
        if (IsInline) flags1 |= SerializerConstants.Attrib1InlineFlag;
        if (IsDiscardable) flags1 |= SerializerConstants.Attrib1DiscardableFlag;
        
        if (VarargsKind == VarargsKind.C) flags2 |= SerializerConstants.Attrib2CStyleVariadicFlag;
        if (VarargsKind == VarargsKind.Laye) flags2 |= SerializerConstants.Attrib2LayeVariadicFlag;

        if (flags2 != 0)
            flags1 |= SerializerConstants.AttribExtensionFlag;

        writer.Write(flags1);
        if (flags2 != 0)
            writer.Write(flags2);

        #endregion

        #region Flags Data

        if (IsForeign)
            serializer.WriteAtom(writer, ForeignSymbolName);

        #endregion

        #region Template Parameters

        if (TemplateParameters is { } templateParams)
        {
            serializer.Context.Todo("Serialize function template parameters");
            throw new UnreachableException();
        }
        // assume we want to write the number of template parameters as a 7-bit encoded int, so start with 0
        else writer.Write((byte)0);

        #endregion

        #region Return Type

        serializer.WriteTypeQual(writer, ReturnType);

        #endregion

        #region Parameters

        writer.Write((ushort)ParameterDecls.Count);
        for (int i = 0; i < ParameterDecls.Count; i++)
        {
            var param = ParameterDecls[i];
            serializer.WriteAtom(writer, param.Name);
            serializer.WriteLocation(writer, param.Location);
            serializer.WriteTypeQual(writer, param.ParamType);

            flags1 = 0;
            flags2 = 0;

            if (param.IsRefParam)
                flags1 |= SerializerConstants.ParamAttrib1Ref;

            writer.Write(flags1);
            if (flags2 != 0)
                writer.Write(flags2);
        }

        #endregion
    }

    public override void Deserialize(ModuleDeserializer deserializer, BinaryReader reader)
    {
        #region Flags

        byte flags1 = reader.ReadByte();
        CallingConvention = (flags1 & SerializerConstants.Attrib1CallingConventionMask) switch
        {
            SerializerConstants.Attrib1CallingConventionCDecl => CallingConvention.CDecl,
            SerializerConstants.Attrib1CallingConventionLaye => CallingConvention.Laye,
            SerializerConstants.Attrib1CallingConventionStdCall => CallingConvention.StdCall,
            SerializerConstants.Attrib1CallingConventionFastCall => CallingConvention.FastCall,
            _ => throw new UnreachableException(),
        };

        IsForeign = 0 != (flags1 & SerializerConstants.Attrib1ForeignFlag);
        IsInline = 0 != (flags1 & SerializerConstants.Attrib1InlineFlag);
        IsDiscardable = 0 != (flags1 & SerializerConstants.Attrib1DiscardableFlag);

        if (0 != (flags1 & SerializerConstants.AttribExtensionFlag))
        {
            byte flags2 = reader.ReadByte();
            if (0 != (flags2 & SerializerConstants.Attrib2CStyleVariadicFlag))
                VarargsKind = VarargsKind.C;
            if (0 != (flags2 & SerializerConstants.Attrib2LayeVariadicFlag))
                VarargsKind = VarargsKind.Laye;
        }

        #endregion

        #region Flags Data

        if (IsForeign)
            ForeignSymbolName = deserializer.ReadAtom(reader);

        #endregion

        #region Template Parameters

        int templateParamCount = reader.Read7BitEncodedInt();
        deserializer.Context.Assert(templateParamCount == 0, "Template parameters are not currently supported in the serializer");

        #endregion

        #region Return Type

        ReturnType = deserializer.ReadTypeQual(reader);

        #endregion

        #region Parameters

        int paramCount = reader.ReadUInt16();
        var paramDecls = new SemaDeclParam[paramCount];
        for (int i = 0; i < paramCount; i++)
        {
            string paramName = deserializer.ReadAtom(reader)!;
            var location = deserializer.ReadLocation(reader);
            var paramType = deserializer.ReadTypeQual(reader);

            flags1 = reader.ReadByte();
            bool isRefParam = 0 != (flags1 & SerializerConstants.ParamAttrib1Ref);

            if (0 != (flags1 & SerializerConstants.AttribExtensionFlag))
            {
                byte flags2 = reader.ReadByte();
            }

            paramDecls[i] = new SemaDeclParam(location, paramName, isRefParam, paramType);
        }

        ParameterDecls = paramDecls;

        #endregion
    }
}

public sealed class SemaDeclDelegate(Location location, string name)
    : SemaDeclNamed(location, name)
{
    public SemaTypeQual ReturnType { get; set; } = SemaTypePoison.Instance.Qualified(Location.Nowhere);
    public IReadOnlyList<SemaDeclParam> ParameterDecls { get; set; } = [];
    public SemaDeclTemplateParameters? TemplateParameters { get; set; }
    
    public CallingConvention CallingConvention { get; set; } = CallingConvention.Laye;
    public VarargsKind VarargsKind { get; set; } = VarargsKind.None;

    public override IEnumerable<BaseSemaNode> Children
    {
        get
        {
            yield return ReturnType;
            foreach (var param in ParameterDecls)
                yield return param;
            if (TemplateParameters is not null)
                yield return TemplateParameters;
        }
    }

    public SemaTypeFunction FunctionType(ChoirContext context) => new SemaTypeFunction(context, ReturnType, ParameterDecls)
    {
        CallingConvention = CallingConvention,
        VarargsKind = VarargsKind,
    };
}

public sealed class SemaDeclBinding(Location location, string name)
    : SemaDeclNamed(location, name)
{
    public SemaTypeQual BindingType { get; set; } = SemaTypePoison.Instance.Qualified(Location.Nowhere);
    public SemaExpr? InitialValue { get; set; }

    public override IEnumerable<BaseSemaNode> Children
    {
        get
        {
            yield return BindingType;
            if (InitialValue is not null)
                yield return InitialValue;
        }
    }
}

public sealed class SemaDeclField(Location location, string name, SemaDeclStruct declaringStruct, SemaTypeQual fieldType, int fieldIndex)
    : SemaDeclNamed(location, name)
{
    public SemaDeclStruct DeclaringStruct { get; } = declaringStruct;
    public SemaTypeQual FieldType { get; } = fieldType;
    public int FieldIndex { get; } = fieldIndex;

    public Size Size => FieldType.Size;
    public Align Align => FieldType.Align;

    public Size Offset
    {
        get
        {
            Size offset = DeclaringStruct.BaseOffset;

            for (int i = 0; i < FieldIndex; i++)
            {
                var declField = DeclaringStruct.FieldDecls[i];
                offset = offset.AlignedTo(declField.Align) + declField.Size;
            }

            return offset.AlignedTo(Align);
        }
    }

    public override IEnumerable<BaseSemaNode> Children { get; } = [fieldType];
}

public sealed class SemaDeclStruct(Location location, string name)
    : SemaDeclNamed(location, name)
{
    public IReadOnlyList<SemaDeclField> FieldDecls { get; set; } = [];
    public IReadOnlyList<SemaDeclStruct> VariantDecls { get; set; } = [];
    public SemaDeclTemplateParameters? TemplateParameters { get; init; }

    public required SemaDeclStruct? ParentStruct { get; init; }
    public required Scope Scope { get; init; }

    public Size Size
    {
        get
        {
            Size size = Size.FromBytes(0);
            Align align = Align.ByteAligned;

            foreach (var field in FieldDecls)
            {
                size = size.AlignedTo(field.Align) + field.Size;
                align = Align.Max(align, field.Align);
            }

            Size largestSizeWithVariants = size;
            foreach (var variant in VariantDecls)
                CalcVariantSize(variant, size, ref largestSizeWithVariants, ref align);

            return largestSizeWithVariants.AlignedTo(align);

            static void CalcVariantSize(SemaDeclStruct variant, Size baseOffset, ref Size largestSizeWithVariants, ref Align structAlign)
            {
                Size variantSize = baseOffset;
                foreach (var field in variant.FieldDecls)
                {
                    variantSize = variantSize.AlignedTo(field.Align) + field.Size;
                    structAlign = Align.Max(structAlign, field.Align);
                }

                foreach (var childVariant in variant.VariantDecls)
                    CalcVariantSize(childVariant, variantSize, ref largestSizeWithVariants, ref structAlign);

                largestSizeWithVariants = Size.Max(largestSizeWithVariants, variantSize);
            }
        }
    }

    public Align Align
    {
        get
        {
            Align align = Align.ByteAligned;

            foreach (var field in FieldDecls)
                align = Align.Max(align, field.Align);

            foreach (var variant in VariantDecls)
                align = Align.Max(align, variant.Align);

            return align;
        }
    }

    public Size BaseOffset
    {
        get
        {
            if (ParentStruct is null)
                return Size.FromBytes(0);

            Size offset = ParentStruct.BaseOffset;
            for (int i = 0; i < ParentStruct.FieldDecls.Count; i++)
            {
                var field = ParentStruct.FieldDecls[i];
                offset = offset.AlignedTo(field.Align) + field.Size;
            }

            return offset;
        }
    }

    public bool IsVariant => ParentStruct is not null;
    public bool IsLeaf => VariantDecls.Count == 0;
    public int VariantTag { get; set; } = -1;
    
    public override IEnumerable<BaseSemaNode> Children
    {
        get
        {
            foreach (var @field in FieldDecls)
                yield return @field;
            foreach (var variant in VariantDecls)
                yield return variant;
            if (TemplateParameters is not null)
                yield return TemplateParameters;
        }
    }

    public bool TryLookupField(string fieldName, [NotNullWhen(true)] out SemaDeclField? declField, out int fieldIndex)
    {
        for (int i = 0; i < FieldDecls.Count; i++)
        {
            if (FieldDecls[i].Name == fieldName)
            {
                declField = FieldDecls[i];
                fieldIndex = i;
                return true;
            }
        }

        declField = null;
        fieldIndex = -1;
        return false;
    }
    public override SerializedDeclKind SerializedDeclKind => IsVariant ? SerializedDeclKind.Variant : SerializedDeclKind.Struct;
    public override void Serialize(ModuleSerializer serializer, BinaryWriter writer)
    {
        #region Template Parameters

        if (TemplateParameters is { } templateParams)
        {
            serializer.Context.Todo("Serialize struct template parameters");
            throw new UnreachableException();
        }
        // assume we want to write the number of template parameters as a 7-bit encoded int, so start with 0
        else writer.Write((byte)0);

        #endregion

        #region Fields

        writer.Write((ushort)FieldDecls.Count);
        for (int i = 0; i < FieldDecls.Count; i++)
        {
            var field = FieldDecls[i];
            serializer.Context.Assert(i == field.FieldIndex, field.Location, $"Field at index {i} in the field declaration array was not properly assigned its index (it was {field.FieldIndex} instead)");

            serializer.WriteAtom(writer, field.Name);
            serializer.WriteLocation(writer, field.Location);
            serializer.WriteTypeQual(writer, field.FieldType);
        }

        #endregion

        #region Variants

        writer.Write((ushort)VariantDecls.Count);
        for (int i = 0; i < VariantDecls.Count; i++)
        {
            var variant = VariantDecls[i];
            writer.Write((byte)SerializedDeclKind.Variant);
            writer.Write(variant.Name);
            serializer.WriteLocation(writer, variant.Location);
            variant.Serialize(serializer, writer);
        }

        #endregion
    }

    public override void Deserialize(ModuleDeserializer deserializer, BinaryReader reader)
    {
        #region Template Parameters

        int templateParamCount = reader.Read7BitEncodedInt();
        deserializer.Context.Assert(templateParamCount == 0, "Template parameters are not currently supported in the serializer");

        #endregion

        #region Fields

        int fieldCount = reader.ReadUInt16();
        var fieldDecls = new SemaDeclField[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            string fieldName = deserializer.ReadAtom(reader)!;
            var location = deserializer.ReadLocation(reader);
            var fieldType = deserializer.ReadTypeQual(reader);
            fieldDecls[i] = new SemaDeclField(location, fieldName, this, fieldType, i);
            Scope.AddDecl(fieldDecls[i]);
        }

        FieldDecls = fieldDecls;

        #endregion

        #region Variants

        int variantCount = reader.ReadUInt16();
        var variantDecls = new SemaDeclStruct[variantCount];
        for (int i = 0; i < variantCount; i++)
        {
            byte sigil = reader.ReadByte();
            deserializer.Context.Assert(sigil == SerializerConstants.VariantSigil, "Tried to deserialize a variant, but was not at a variant sigil.");

            string variantName = deserializer.ReadAtom(reader)!;
            var location = deserializer.ReadLocation(reader);
            var variant = new SemaDeclStruct(location, variantName)
            {
                ParentStruct = this,
                Scope = new(Scope)
            };

            variant.Deserialize(deserializer, reader);
        }

        VariantDecls = variantDecls;

        #endregion
    }
}

public sealed class SemaDeclEnumVariant(Location location, string name, BigInteger value)
    : SemaDeclNamed(location, name)
{
    public BigInteger Value { get; } = value;
}

public sealed class SemaDeclEnum(Location location, string name)
    : SemaDeclNamed(location, name)
{
    public IReadOnlyList<SemaDeclEnumVariant> Variants { get; set; } = [];

    public required Scope Scope { get; init; }

    public override IEnumerable<BaseSemaNode> Children
    {
        get
        {
            foreach (var variant in Variants)
                yield return variant;
        }
    }
}

public sealed class SemaDeclAlias(Location location, string name, bool isStrict = false)
    : SemaDeclNamed(location, name)
{
    public bool IsStrict { get; set; } = isStrict;
    public SemaTypeQual AliasedType { get; set; } = SemaTypePoison.Instance.Qualified(Location.Nowhere);
    public SemaDeclTemplateParameters? TemplateParameters { get; set; }

    public override IEnumerable<BaseSemaNode> Children
    {
        get
        {
            yield return AliasedType;
            if (TemplateParameters is not null)
                yield return TemplateParameters;
        }
    }
}

public sealed class SemaDeclRegister(Location location, string registerName, string name)
    : SemaDeclNamed(location, name)
{
    public string RegisterName { get; } = registerName;
    public SemaTypeQual Type { get; set; } = SemaTypePoison.Instance.Qualified(Location.Nowhere);
    public override IEnumerable<BaseSemaNode> Children => [Type];
}

public sealed class SemaDeclTest(Location location, string description, SemaStmt body)
    : SemaDecl(location)
{
    public string Description { get; } = description;
    public SemaStmt Body { get; } = body;
}
