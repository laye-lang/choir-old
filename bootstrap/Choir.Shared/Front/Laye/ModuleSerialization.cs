using System.Diagnostics;
using System.Text;

using Choir.Front.Laye.Sema;

namespace Choir.Front.Laye;

internal static class SerializerConstants
{
    // The magic number is "LAYEMOD1" -- this is it as little endian ASCII bytes.
    public const ulong ModuleMagicNumber = 0x31444F4D4559414CUL;

    public const long Alignment = 4;

    public const string AtomChunkName = "Atom";
    public const string FileChunkName = "File";
    public const string TypeChunkName = "Type";
    public const string DeclChunkName = "Decl";
    public const string CodeChunkName = "Code";

    public const char FunctionSigil = 'F';
    public const char StructSigil = 'S';
    public const char VariantSigil = 'V';
    public const char EnumSigil = 'E';
    public const char AliasSigil = 'A';
    public const char DelegateSigil = 'D';

    public const char DeclNameSimpleSigil = 'n';

    public const char QualifiedTypeSigil = 'Q';
    public const char VoidTypeSigil = 'v';
    public const char NoReturnTypeSigil = 'V';
    public const char BoolTypeSigil = 'b';
    public const char BoolSizedTypeSigil = 'B';
    public const char IntTypeSigil = 'i';
    public const char IntSizedTypeSigil = 'I';
    public const char Float32TypeSigil = 'f';
    public const char Float64TypeSigil = 'd';
    public const char FFIPrefixTypeSigil = 'C';
    public const char FFIBoolTypeSigil = 'b';
    public const char FFICharTypeSigil = 'c';
    public const char FFIShortTypeSigil = 's';
    public const char FFIIntTypeSigil = 'i';
    public const char FFILongTypeSigil = 'l';
    public const char FFILongLongTypeSigil = 'L';
    public const char FFIFloatTypeSigil = 'f';
    public const char FFIDoubleTypeSigil = 'd';
    public const char FFILongDoubleTypeSigil = 'D';

    public const char PointerTypeSigil = '*';
    public const char BufferTypeSigil = '^';
    public const char ReferenceTypeSigil = '&';
    public const char SliceTypeSigil = ']';
    public const char ArrayTypeSigil = '[';
    public const char NilableTypeSigil = '?';

    public const byte MutableFlag = 1 << 0;

    public const byte AttribExtensionFlag = 1 << 7;

    public const byte Attrib1CallingConventionMask = 0x0F;
    public const byte Attrib1CallingConventionCDecl = 0x00;
    public const byte Attrib1CallingConventionLaye = 0x01;
    public const byte Attrib1CallingConventionStdCall = 0x02;
    public const byte Attrib1CallingConventionFastCall = 0x03;
    public const byte Attrib1ForeignFlag = 1 << 4;
    public const byte Attrib1InlineFlag = 1 << 5;
    public const byte Attrib1DiscardableFlag = 1 << 6;

    public const byte Attrib2CStyleVariadicFlag = 1 << 0;
    public const byte Attrib2LayeVariadicFlag = 1 << 1;

    public const byte ParamAttrib1Ref = 1 << 0;
}

public enum SerializedDeclKind : byte
{
    Invalid = 0,
    Function = (byte)SerializerConstants.FunctionSigil,
    Struct = (byte)SerializerConstants.StructSigil,
    Variant = (byte)SerializerConstants.VariantSigil,
    Enum = (byte)SerializerConstants.EnumSigil,
    Alias = (byte)SerializerConstants.AliasSigil,
    Delegate = (byte)SerializerConstants.DelegateSigil,
}

public enum SerializedTypeKind : byte
{
    Invalid = 0,
    Qualified = (byte)SerializerConstants.QualifiedTypeSigil,
    Void = (byte)SerializerConstants.VoidTypeSigil,
    NoReturn = (byte)SerializerConstants.NoReturnTypeSigil,
    Bool = (byte)SerializerConstants.BoolTypeSigil,
    BoolSized = (byte)SerializerConstants.BoolSizedTypeSigil,
    Int = (byte)SerializerConstants.IntTypeSigil,
    IntSized = (byte)SerializerConstants.IntSizedTypeSigil,
    Float32 = (byte)SerializerConstants.Float32TypeSigil,
    Float64 = (byte)SerializerConstants.Float64TypeSigil,
    FFI = (byte)SerializerConstants.FFIPrefixTypeSigil,
    Pointer = (byte)SerializerConstants.PointerTypeSigil,
    Buffer = (byte)SerializerConstants.BufferTypeSigil,
    Reference = (byte)SerializerConstants.ReferenceTypeSigil,
    Slice = (byte)SerializerConstants.SliceTypeSigil,
    Array = (byte)SerializerConstants.ArrayTypeSigil,
    Nilable = (byte)SerializerConstants.NilableTypeSigil,
}

public readonly record struct SerializedModuleHeader(
    ulong Magic, string ModuleName, IReadOnlyList<string> DependencyNames, IReadOnlyList<string> LinkLibraryNames, long DataSize);

public sealed class ModuleSerializer : IDisposable
{
    public static byte[] SerializeToBytes(ChoirContext context, LayeModule module)
    {
        using var memoryStream = new MemoryStream();
        SerializeToStream(context, module, memoryStream);
        memoryStream.Flush();
        return memoryStream.ToArray();
    }

    public static void SerializeToStream(ChoirContext context, LayeModule module, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, false);
        using var s = new ModuleSerializer(context, module);
        s.Serialize(writer);
    }

    public ChoirContext Context { get; }
    public LayeModule Module { get; }

    private readonly Dictionary<string, BinaryWriter> _chunkWriters = [];

    private readonly Dictionary<int, long> _sourceFileIndices;

    private readonly List<string> _atoms = [];
    private readonly Dictionary<string, long> _atomIndices = [];

    private readonly List<SemaType> _types = [];
    private readonly Dictionary<SemaType, (long Index, BinaryWriter Writer)> _typeWriters = [];

    private readonly List<SemaDeclNamed> _decls = [];
    private readonly Dictionary<SemaDeclNamed, (long Index, BinaryWriter Writer)> _declWriters = [];

    private ModuleSerializer(ChoirContext context, LayeModule module)
    {
        Context = context;
        Module = module;

        _sourceFileIndices = module.SourceFiles.Select((sf, i) => (sf, i))
            .ToDictionary(pair => pair.sf.FileId, pair => (long)pair.i);
    }

    public void Dispose()
    {
    }

    public void WriteAtom(BinaryWriter writer, string? atom)
    {
        if (atom is null)
        {
            writer.Write((byte)0);
            return;
        }

        if (!_atomIndices.TryGetValue(atom, out long index))
        {
            _atomIndices[atom] = index = _atoms.Count + 1;
            _atoms.Add(atom);
        }

        writer.Write7BitEncodedInt64(index);
    }

    public void WriteTypeQual(BinaryWriter writer, SemaTypeQual typeQual)
    {
        long typeIndex = SerializeType(typeQual.Type);

        #region Type & Location

        writer.Write7BitEncodedInt64(typeIndex);
        WriteLocation(writer, typeQual.Location);

        #endregion

        #region Flags

        byte flags = 0;

        if (typeQual.Qualifiers.HasFlag(TypeQualifiers.Mutable))
            flags |= SerializerConstants.MutableFlag;

        writer.Write(flags);

        #endregion
    }

    public void WriteDeclRef(BinaryWriter writer, SemaDeclNamed decl)
    {
        long declIndex = SerializeDecl(decl);
        writer.Write7BitEncodedInt64(declIndex);
    }

    public void WriteDeclName(BinaryWriter writer, string name)
    {
        writer.Write(SerializerConstants.DeclNameSimpleSigil);
        WriteAtom(writer, name);
    }

    public void WriteLocation(BinaryWriter writer, Location location)
    {
        Context.Assert(_sourceFileIndices.ContainsKey(location.FileId), "This location does not exist within the current module.");
        long fileIndex = _sourceFileIndices[location.FileId];
        writer.Write7BitEncodedInt64(fileIndex);
        writer.Write7BitEncodedInt(location.Offset);
        writer.Write7BitEncodedInt(location.Length);
    }

    private void WritePadding(BinaryWriter writer, long size)
    {
        long numPaddingBytes = Align.AlignPadding(size, SerializerConstants.Alignment);
        Context.Assert(numPaddingBytes < SerializerConstants.Alignment, $"Writing too many padding bytes: Expected to write less than {SerializerConstants.Alignment} padding bytes, but writing {numPaddingBytes}.");
        for (long i = 0; i < numPaddingBytes; i++)
            writer.Write((byte)0);
    }

    private BinaryWriter GetOrCreateChunkWriter(string chunkName)
    {
        Context.Assert(chunkName.Length == 4, "Chunk names bust be 4 characters long.");
        if (!_chunkWriters.TryGetValue(chunkName, out var chunkWriter))
            _chunkWriters[chunkName] = chunkWriter = new BinaryWriter(new MemoryStream(1024), Encoding.UTF8, false);
        return chunkWriter;
    }

    private void WriteChunkToModuleData(BinaryWriter writer, string chunkName)
    {
        Context.Assert(chunkName.Length == 4, "Chunk names bust be 4 characters long.");
        Context.Assert(_chunkWriters.ContainsKey(chunkName), $"There is no chunk data for the requested chunk '{chunkName}'.");
        var chunkMemoryStream = (MemoryStream)_chunkWriters[chunkName].BaseStream;
        Context.Assert(chunkMemoryStream.Length >= 0, "The size of a chunk must be non-negative. How did this happen?");
        long chunkSize = chunkMemoryStream.Length;

        byte[] chunkNameBytes = Encoding.UTF8.GetBytes(chunkName);
        Context.Assert(chunkNameBytes.Length == 4, $"Expected 4 bytes from chunk name, got {chunkNameBytes.Length}.");
        writer.Write(chunkNameBytes);
        writer.Write(chunkSize);
        writer.Write(chunkMemoryStream.ToArray());
        WritePadding(writer, chunkSize);
    }

    private long GetChunkTotalSize(string chunkName)
    {
        Context.Assert(chunkName.Length == 4, "Chunk names bust be 4 characters long.");
        Context.Assert(_chunkWriters.ContainsKey(chunkName), $"There is no chunk data for the requested chunk '{chunkName}'.");
        var chunkMemoryStream = (MemoryStream)_chunkWriters[chunkName].BaseStream;

        Context.Assert(chunkMemoryStream.Length >= 0, "The size of a chunk must be non-negative. How did this happen?");
        long chunkSize = chunkMemoryStream.Length;

        return (4 * sizeof(byte)) + sizeof(long) + Align.AlignTo(chunkSize, SerializerConstants.Alignment);
    }

    private void WriteModuleHeader(BinaryWriter writer, long size)
    {
        writer.Write(SerializerConstants.ModuleMagicNumber);
        writer.Write7BitEncodedInt(Module.Dependencies.Count);
        writer.Write(Module.ModuleName);
        foreach (var moduleDep in Module.Dependencies)
            writer.Write(moduleDep.ModuleName);
        WritePadding(writer, writer.BaseStream.Position);
        writer.Write7BitEncodedInt(Module.LinkLibraries.Count);
        foreach (string moduleLib in Module.LinkLibraries)
            writer.Write(moduleLib);
        WritePadding(writer, writer.BaseStream.Position);
        writer.Write(size);
    }

    private void Serialize(BinaryWriter writer)
    {
        #region Generate File chunk

        var fileChunk = GetOrCreateChunkWriter(SerializerConstants.FileChunkName);
        fileChunk.Write7BitEncodedInt(Module.SourceFiles.Count);
        for (int i = 0; i < Module.SourceFiles.Count; i++)
        {
            var sourceFile = Module.SourceFiles[i];
            fileChunk.Write(sourceFile.FilePath);
            if (sourceFile.IsTextless || Context.OmitSourceTextInModuleBinary)
                fileChunk.Write((byte)0); // ReadString should see this as an empty string, I assume
            else fileChunk.Write(sourceFile.Text);
        }

        #endregion

        #region Serialize the AST

        foreach (var decl in Module.ExportedDeclarations)
            SerializeDecl(decl);

        #endregion

        #region Generate Atom chunk

        var atomChunk = GetOrCreateChunkWriter(SerializerConstants.AtomChunkName);
        atomChunk.Write7BitEncodedInt(_atoms.Count);
        for (int i = 0; i < _atoms.Count; i++)
            atomChunk.Write(_atoms[i]);

        #endregion

        #region Generate the Type chunk

        var typeChunk = GetOrCreateChunkWriter(SerializerConstants.TypeChunkName);
        typeChunk.Write7BitEncodedInt(_types.Count);
        for (int i = 0; i < _types.Count; i++)
        {
            var type = _types[i];
            var typeData = _typeWriters[type];
            Context.Assert(i == typeData.Index, "Type indices did not match when serializing module AST.");
            var typeMemory = (MemoryStream)typeData.Writer.BaseStream;
            //typeChunk.Write7BitEncodedInt64(typeMemory.Length);
            typeChunk.Write(typeMemory.GetBuffer(), 0, (int)typeMemory.Length);
        }

        #endregion

        #region Generate the Decl chunk

        var declChunk = GetOrCreateChunkWriter(SerializerConstants.DeclChunkName);
        declChunk.Write7BitEncodedInt(_decls.Count);
        for (int i = 0; i < _decls.Count; i++)
        {
            var decl = _decls[i];
            var declData = _declWriters[decl];
            Context.Assert(i == declData.Index, "Declaration indices did not match when serializing module AST.");
            var declMemory = (MemoryStream)declData.Writer.BaseStream;
            //declChunk.Write7BitEncodedInt64(declMemory.Length);
            declChunk.Write(declMemory.GetBuffer(), 0, (int)declMemory.Length);
        }

        #endregion

        #region Finally, write the bytes to the actual output.

        var dataStream = new MemoryStream(64 * 1024);
        var dataWriter = new BinaryWriter(dataStream, Encoding.UTF8);

        foreach (string chunkName in _chunkWriters.Keys)
            WriteChunkToModuleData(dataWriter, chunkName);

        var comp = new ZstdSharp.Compressor();
        Span<byte> compressedModuleData = comp.Wrap(dataStream.GetBuffer().AsSpan()[..(int)dataStream.Length]);

        WriteModuleHeader(writer, compressedModuleData.Length);
        writer.Write(compressedModuleData);

        #endregion
    }

    private long SerializeType(SemaType type)
    {
        if (_typeWriters.TryGetValue(type, out var data))
            return data.Index;

        var writer = new BinaryWriter(new MemoryStream(64), Encoding.UTF8, false);
        writer.Write((byte)type.SerializedTypeKind);
        type.Serialize(this, writer);

        long index = _types.Count;
        _types.Add(type);
        _typeWriters[type] = (index, writer);

        return index;
    }

    private long SerializeDecl(SemaDeclNamed decl)
    {
        if (_declWriters.TryGetValue(decl, out var data))
            return data.Index;

        long index = _decls.Count;
        _decls.Add(decl);

        var writer = new BinaryWriter(new MemoryStream(64), Encoding.UTF8, false);
        _declWriters[decl] = (index, writer);

        writer.Write((byte)decl.SerializedDeclKind);
        WriteDeclName(writer, decl.Name);
        WriteLocation(writer, decl.Location);
        writer.Write((ushort)0);

        long declDataStartPosition = writer.BaseStream.Position;
        decl.Serialize(this, writer);

        long declDataSize = writer.BaseStream.Position - declDataStartPosition;
        writer.BaseStream.Seek(declDataStartPosition - sizeof(short), SeekOrigin.Begin);
        writer.Write((short)declDataSize);

        return index;
    }
}

public sealed class ModuleDeserializer : IDisposable
{
    public static SerializedModuleHeader DeserializeHeaderFromStream(ChoirContext context, Stream stream)
    {
        using var reader = new BinaryReader(stream);
        var d = new ModuleDeserializer(context, []);
        return d.ReadModuleHeader(reader);
    }

    public static LayeModule DeserializeFromStream(ChoirContext context, LayeModule[] dependencies, Stream stream)
    {
        using var reader = new BinaryReader(stream);
        var d = new ModuleDeserializer(context, dependencies);
        return d.Deserialize(reader);
    }

    public ChoirContext Context { get; }
    private readonly LayeModule[] _dependencies;

    private readonly Dictionary<string, MemoryStream> _chunkMemory = [];

    private SourceFile[] _files = [];
    private string[] _atoms = [];
    private SemaDeclNamed[] _decls = [];
    private SemaType[] _types = [];

    private ModuleDeserializer(ChoirContext context, LayeModule[] dependencies)
    {
        Context = context;
        _dependencies = dependencies;
    }

    public void Dispose()
    {
    }

    public string? ReadAtom(BinaryReader reader)
    {
        long atomIndex = reader.Read7BitEncodedInt64();
        if (atomIndex == 0) return null;
        return _atoms[atomIndex - 1];
    }

    public string ReadDeclName(BinaryReader reader)
    {
        char declNameSigil = reader.ReadChar();
        switch (declNameSigil)
        {
            default:
            {
                Context.Assert(false, $"Unrecognized or unsupported declaration sigil '{declNameSigil}' ({(int)declNameSigil}).");
                throw new UnreachableException();
            }

            case SerializerConstants.DeclNameSimpleSigil: return ReadAtom(reader)!;
        }
    }

    public Location ReadLocation(BinaryReader reader)
    {
        long fileIndex = reader.Read7BitEncodedInt64();
        int offset = reader.Read7BitEncodedInt();
        int length = reader.Read7BitEncodedInt();
        Context.Assert(fileIndex < _files.Length, "This location does not exist within the current module.");
        int fileId = _files[fileIndex].FileId;
        return new(offset, length, fileId);
    }

    public SemaTypeQual ReadTypeQual(BinaryReader reader)
    {
        #region Type & Location

        long typeIndex = reader.Read7BitEncodedInt64();
        var type = _types[typeIndex];
        var location = ReadLocation(reader);

        #endregion

        #region Flags

        byte flags = reader.ReadByte();

        var q = TypeQualifiers.None;
        if (0 != (flags & SerializerConstants.MutableFlag))
            q |= TypeQualifiers.Mutable;

        #endregion

        return new SemaTypeQual(type, location, q);
    }

    private LayeModule Deserialize(BinaryReader reader)
    {
        var header = ReadModuleHeader(reader);

        //ReadOnlySpan<byte> compressedDataBytes = ((MemoryStream)reader.BaseStream).GetBuffer().AsSpan().Slice((int)reader.BaseStream.Position, (int)header.DataSize);
        ReadOnlySpan<byte> compressedDataBytes = reader.ReadBytes((int)header.DataSize);

        ulong decompressedDataSize = ZstdSharp.Decompressor.GetDecompressedSize(compressedDataBytes);
        byte[] decompressedData = new byte[decompressedDataSize];

        var decompressor = new ZstdSharp.Decompressor();
        int _ = decompressor.Unwrap(compressedDataBytes, decompressedData.AsSpan());

        var dataChunkMemory = new MemoryStream(decompressedData);
        var dataChunkReader = new BinaryReader(dataChunkMemory, Encoding.UTF8, false);

        byte[] chunkNameBytes = new byte[4];
        while (4 == dataChunkReader.Read(chunkNameBytes, 0, 4))
        {
            string chunkName = Encoding.UTF8.GetString(chunkNameBytes);
            long chunkSize = dataChunkReader.ReadInt64();

            long chunkStartPosition = dataChunkReader.BaseStream.Position;
            var chunkMemory = new MemoryStream(decompressedData, (int)chunkStartPosition, (int)chunkSize);
            _chunkMemory[chunkName] = chunkMemory;

            dataChunkReader.BaseStream.Position += chunkSize;
            ReadPadding(dataChunkReader, dataChunkReader.BaseStream.Position);
        }

        PopulateFileTable();
        PopulateAtomTable();

        var module = new LayeModule(Context, _files, _dependencies)
        {
            ModuleName = header.ModuleName
        };

        ForwardDeclareDecls(module);
        DeserializeTypes(module);
        DeserializeDecls(module);

        foreach (var decl in _decls)
            module.ExportScope.AddDecl(decl);

        foreach (string linkLibrary in header.LinkLibraryNames)
            module.AddLinkLibrary(linkLibrary);

        return module;
    }

    private static void ReadRemaining(BinaryReader reader, long startPosition, long currentPosition, long length)
    {
        long endPosition = startPosition + length;
        long readCount = endPosition - currentPosition;
        if (readCount > 0) reader.ReadBytes((int)readCount);
    }

    private static void ReadPadding(BinaryReader reader, long nRead)
    {
        int paddingCount = (int)Align.AlignPadding(nRead, SerializerConstants.Alignment);
        if (paddingCount > 0) reader.ReadBytes(paddingCount);
    }

    private SerializedModuleHeader ReadModuleHeader(BinaryReader reader)
    {
        ulong magic = reader.ReadUInt64();
        Context.Assert(magic == SerializerConstants.ModuleMagicNumber, "Only the LAYEMOD1 serialized module version is supported.");

        //long headerStartPosition = reader.BaseStream.Position;
        //int headerLength = reader.Read7BitEncodedInt();

        int dependencyCount = reader.Read7BitEncodedInt();
        string moduleName = reader.ReadString();

        string[] dependencyNames = new string[dependencyCount];
        for (int i = 0; i < dependencyCount; i++)
            dependencyNames[i] = reader.ReadString();

        ReadPadding(reader, reader.BaseStream.Position);

        int linkLibraryCount = reader.Read7BitEncodedInt();
        string[] linkLibraryNames = new string[linkLibraryCount];
        for (int i = 0; i < linkLibraryCount; i++)
            linkLibraryNames[i] = reader.ReadString();

        //ReadRemaining(reader, headerStartPosition, reader.BaseStream.Position, headerLength);
        ReadPadding(reader, reader.BaseStream.Position);

        long dataSize = reader.ReadInt64();

        return new(magic, moduleName, dependencyNames, linkLibraryNames, dataSize);
    }

    private void PopulateFileTable()
    {
        if (!_chunkMemory.TryGetValue(SerializerConstants.FileChunkName, out var fileMemory))
            return;

        var fileReader = new BinaryReader(fileMemory, Encoding.UTF8, false);

        int fileCount = fileReader.Read7BitEncodedInt();
        _files = new SourceFile[fileCount];

        for (int i = 0; i < fileCount; i++)
        {
            string filePath = fileReader.ReadString();
            string fileText = fileReader.ReadString();

            if (fileText.IsNullOrEmpty())
                _files[i] = Context.GetSourceFileRef(filePath, null);
            else _files[i] = Context.GetSourceFileRef(filePath, fileText);
        }
    }

    private void PopulateAtomTable()
    {
        if (!_chunkMemory.TryGetValue(SerializerConstants.AtomChunkName, out var atomMemory))
            return;

        var atomReader = new BinaryReader(atomMemory, Encoding.UTF8, false);
        
        int atomCount = atomReader.Read7BitEncodedInt();
        _atoms = new string[atomCount];

        for (int i = 0; i < atomCount; i++)
            _atoms[i] = atomReader.ReadString();
    }

    private void ForwardDeclareDecls(LayeModule module)
    {
        if (!_chunkMemory.TryGetValue(SerializerConstants.DeclChunkName, out var declMemory))
            return;

        var declReader = new BinaryReader(declMemory, Encoding.UTF8, false);

        int declCount = declReader.Read7BitEncodedInt();
        _decls = new SemaDeclNamed[declCount];

        for (int i = 0; i < declCount; i++)
        {
            var declKind = (SerializedDeclKind)declReader.ReadByte();

            string declName = ReadDeclName(declReader);
            var location = ReadLocation(declReader);

            SemaDeclNamed forwardDecl;
            switch (declKind)
            {
                default:
                {
                    Context.Diag.ICE($"Attempt to deserialize named declaration of type {declKind}, which is currently not supported.");
                    throw new UnreachableException();
                }

                case SerializedDeclKind.Function: forwardDecl = new SemaDeclFunction(location, declName) { Scope = null! }; break;
                case SerializedDeclKind.Struct: forwardDecl = new SemaDeclStruct(location, declName)
                {
                    ParentStruct = null,
                    Scope = new(module.ModuleScope)
                }; break;
            }

            _decls[i] = forwardDecl;

            int declDataSize = declReader.ReadUInt16();
            declReader.BaseStream.Position += declDataSize;
        }

        declMemory.Position = 0;
    }

    private void DeserializeTypes(LayeModule module)
    {
        if (!_chunkMemory.TryGetValue(SerializerConstants.TypeChunkName, out var typeMemory))
            return;

        var typeReader = new BinaryReader(typeMemory, Encoding.UTF8, false);

        int typeCount = typeReader.Read7BitEncodedInt();
        _types = new SemaType[typeCount];

        for (int i = 0; i < typeCount; i++)
        {
            var typeKind = (SerializedTypeKind)typeReader.ReadByte();
            _types[i] = SemaType.Deserialize(this, typeKind, typeReader);
        }
    }

    private void DeserializeDecls(LayeModule module)
    {
        if (!_chunkMemory.TryGetValue(SerializerConstants.DeclChunkName, out var declMemory))
            return;

        var declReader = new BinaryReader(declMemory, Encoding.UTF8, false);
        int declCount = declReader.Read7BitEncodedInt();
        Context.Assert(declCount == _decls.Length, "Number of decls changed somewhere");

        for (int i = 0; i < declCount; i++)
        {
            var declKind = (SerializedDeclKind)declReader.ReadByte();
            var decl = _decls[i];
            Context.Assert(decl.SerializedDeclKind == declKind, "Decl kind changed somewhere");

            string declName = ReadDeclName(declReader);
            Context.Assert(declName == decl.Name, "Decl name changed somewhere");
            var _ = ReadLocation(declReader); // less important to assert, and the other two should have caught weird bugs anyway.

            int declDataSize = declReader.ReadUInt16();
            long currentDeclDataPosition = declReader.BaseStream.Position;

            decl.Deserialize(this, declReader);
            Context.Assert(currentDeclDataPosition + declDataSize == declReader.BaseStream.Position, $"Expected to read {declDataSize} bytes during decl serialization, but read {declReader.BaseStream.Position - currentDeclDataPosition}.");

            decl.Linkage = Linkage.Imported;
        }
    }
}
