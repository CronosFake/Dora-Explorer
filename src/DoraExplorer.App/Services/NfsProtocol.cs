using System.IO;

namespace DoraExplorer.App.Services;

/// <summary>Códigos de resultado do NFS versão 3 (RFC 1813, 2.6).</summary>
internal enum NfsStatus : uint
{
    Ok = 0,
    Perm = 1,
    NoEnt = 2,
    Io = 5,
    NxIo = 6,
    Access = 13,
    Exist = 17,
    XDev = 18,
    NoDev = 19,
    NotDir = 20,
    IsDir = 21,
    Inval = 22,
    FBig = 27,
    NoSpc = 28,
    RoFs = 30,
    MLink = 31,
    NameTooLong = 63,
    NotEmpty = 66,
    DQuot = 69,
    Stale = 70,
    Remote = 71,
    BadHandle = 10001,
    NotSync = 10002,
    BadCookie = 10003,
    NotSupp = 10004,
    TooSmall = 10005,
    ServerFault = 10006,
    BadType = 10007,
    JukeBox = 10008
}

internal enum NfsFileType : uint
{
    Regular = 1,
    Directory = 2,
    Block = 3,
    Character = 4,
    Link = 5,
    Socket = 6,
    Fifo = 7
}

/// <summary>Falha devolvida pelo servidor NFS (ou pelo MOUNT), com a mensagem já em português.</summary>
internal sealed class NfsException(NfsStatus status, string message) : IOException(message)
{
    public NfsStatus Status { get; } = status;

    public static NfsException From(NfsStatus status, string operation) =>
        new(status, $"{operation}: {Describe(status)}");

    public static string Describe(NfsStatus status) =>
        status switch
        {
            NfsStatus.Perm => "operação não permitida",
            NfsStatus.NoEnt => "o item não existe",
            NfsStatus.Io => "erro de leitura ou gravação no servidor",
            NfsStatus.NxIo => "dispositivo inexistente",
            NfsStatus.Access => "acesso negado pelo servidor",
            NfsStatus.Exist => "já existe um item com esse nome",
            NfsStatus.XDev => "não dá para mover entre compartilhamentos diferentes",
            NfsStatus.NoDev => "dispositivo inexistente",
            NfsStatus.NotDir => "o item não é uma pasta",
            NfsStatus.IsDir => "o item é uma pasta",
            NfsStatus.Inval => "pedido inválido",
            NfsStatus.FBig => "arquivo grande demais para o servidor",
            NfsStatus.NoSpc => "sem espaço no servidor",
            NfsStatus.RoFs => "o compartilhamento é somente leitura",
            NfsStatus.NameTooLong => "nome longo demais",
            NfsStatus.NotEmpty => "a pasta não está vazia",
            NfsStatus.DQuot => "cota de espaço esgotada",
            NfsStatus.Stale or NfsStatus.BadHandle => "o item mudou ou foi removido no servidor",
            NfsStatus.NotSupp => "o servidor não oferece esta operação",
            NfsStatus.JukeBox => "o servidor está ocupado, tente de novo",
            _ => $"o servidor respondeu com o erro {(uint)status}"
        };
}

/// <summary>Atributos de um arquivo (fattr3) no que o programa usa.</summary>
internal sealed record NfsAttributes(
    NfsFileType Type,
    uint Mode,
    uint Uid,
    uint Gid,
    ulong Size,
    ulong Used,
    ulong FileId,
    DateTime ModifiedLocal)
{
    public bool IsDirectory => Type == NfsFileType.Directory;

    public bool IsLink => Type == NfsFileType.Link;
}

/// <summary>Uma entrada de pasta; <see cref="Attributes"/> e <see cref="Handle"/> só vêm no READDIRPLUS.</summary>
internal sealed record NfsDirectoryEntry(string Name, ulong FileId, NfsAttributes? Attributes, byte[]? Handle);

/// <summary>Atributos a alterar (sattr3): campos nulos não mudam.</summary>
internal sealed record NfsSetAttributes(uint? Mode = null, ulong? Size = null, DateTime? ModifiedUtc = null);

/// <summary>Nomes de procedimento, estruturas XDR e conversões do NFSv3, compartilhados pelo cliente e pelo servidor.</summary>
internal static class NfsProtocol
{
    public const uint NfsVersion = 3;
    public const uint MountVersion = 3;

    public const int FileHandleMaxBytes = 64;
    public const int NameMaxBytes = 255;
    public const int PathMaxBytes = 1024;
    public const int AttributesBytes = 84;
    public const int VerifierBytes = 8;

    // Procedimentos do programa NFS v3.
    public const uint ProcNull = 0;
    public const uint ProcGetAttr = 1;
    public const uint ProcSetAttr = 2;
    public const uint ProcLookup = 3;
    public const uint ProcAccess = 4;
    public const uint ProcReadLink = 5;
    public const uint ProcRead = 6;
    public const uint ProcWrite = 7;
    public const uint ProcCreate = 8;
    public const uint ProcMkDir = 9;
    public const uint ProcSymlink = 10;
    public const uint ProcMkNod = 11;
    public const uint ProcRemove = 12;
    public const uint ProcRmDir = 13;
    public const uint ProcRename = 14;
    public const uint ProcLink = 15;
    public const uint ProcReadDir = 16;
    public const uint ProcReadDirPlus = 17;
    public const uint ProcFsStat = 18;
    public const uint ProcFsInfo = 19;
    public const uint ProcPathConf = 20;
    public const uint ProcCommit = 21;

    // Procedimentos do MOUNT v3.
    public const uint MountProcNull = 0;
    public const uint MountProcMnt = 1;
    public const uint MountProcDump = 2;
    public const uint MountProcUmnt = 3;
    public const uint MountProcUmntAll = 4;
    public const uint MountProcExport = 5;

    // Bits de ACCESS3.
    public const uint AccessRead = 0x01;
    public const uint AccessLookup = 0x02;
    public const uint AccessModify = 0x04;
    public const uint AccessExtend = 0x08;
    public const uint AccessDelete = 0x10;
    public const uint AccessExecute = 0x20;

    public const uint StableUnstable = 0;
    public const uint StableDataSync = 1;
    public const uint StableFileSync = 2;

    public static DateTime FromNfsTime(uint seconds, uint nanoseconds) =>
        DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanoseconds / 100).ToLocalTime();

    public static (uint Seconds, uint Nanoseconds) ToNfsTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var delta = utc - DateTime.UnixEpoch;
        if (delta < TimeSpan.Zero)
        {
            return (0, 0);
        }

        var seconds = (long)delta.TotalSeconds;
        return ((uint)Math.Min(seconds, uint.MaxValue), (uint)(delta.Ticks % TimeSpan.TicksPerSecond * 100));
    }

    public static NfsAttributes ReadAttributes(XdrReader reader)
    {
        var type = (NfsFileType)reader.UInt32();
        var mode = reader.UInt32();
        _ = reader.UInt32(); // nlink
        var uid = reader.UInt32();
        var gid = reader.UInt32();
        var size = reader.UInt64();
        var used = reader.UInt64();
        _ = reader.UInt64(); // rdev
        _ = reader.UInt64(); // fsid
        var fileId = reader.UInt64();
        _ = reader.UInt32(); // atime
        _ = reader.UInt32();
        var mtimeSeconds = reader.UInt32();
        var mtimeNanoseconds = reader.UInt32();
        _ = reader.UInt32(); // ctime
        _ = reader.UInt32();
        return new NfsAttributes(type, mode, uid, gid, size, used, fileId, FromNfsTime(mtimeSeconds, mtimeNanoseconds));
    }

    public static NfsAttributes? ReadPostOpAttributes(XdrReader reader) =>
        reader.Bool() ? ReadAttributes(reader) : null;

    /// <summary>Pula o wcc_data (atributos de antes e de depois de uma alteração).</summary>
    public static void SkipWccData(XdrReader reader)
    {
        if (reader.Bool())
        {
            _ = reader.Fixed(24); // tamanho, mtime e ctime de antes
        }

        _ = ReadPostOpAttributes(reader);
    }

    public static void WriteAttributes(XdrWriter writer, NfsFileType type, uint mode, uint uid, uint gid, ulong size, ulong used,
        ulong fileId, DateTime modifiedUtc, DateTime createdUtc, ulong fsid)
    {
        var (mSec, mNsec) = ToNfsTime(modifiedUtc);
        var (cSec, cNsec) = ToNfsTime(createdUtc);
        writer.UInt32((uint)type).UInt32(mode).UInt32(type == NfsFileType.Directory ? 2u : 1u).UInt32(uid).UInt32(gid);
        writer.UInt64(size).UInt64(used);
        writer.UInt64(0); // rdev
        writer.UInt64(fsid).UInt64(fileId);
        writer.UInt32(mSec).UInt32(mNsec); // atime (repete a modificação: o Windows não é consultado)
        writer.UInt32(mSec).UInt32(mNsec);
        writer.UInt32(cSec).UInt32(cNsec);
    }

    public static void WriteSetAttributes(XdrWriter writer, NfsSetAttributes attributes)
    {
        writer.Bool(attributes.Mode.HasValue);
        if (attributes.Mode is { } mode)
        {
            writer.UInt32(mode);
        }

        writer.Bool(false); // uid
        writer.Bool(false); // gid
        writer.Bool(attributes.Size.HasValue);
        if (attributes.Size is { } size)
        {
            writer.UInt64(size);
        }

        writer.UInt32(0); // atime: não muda
        if (attributes.ModifiedUtc is { } modified)
        {
            var (seconds, nanoseconds) = ToNfsTime(modified);
            writer.UInt32(2).UInt32(seconds).UInt32(nanoseconds);
        }
        else
        {
            writer.UInt32(0);
        }
    }

    /// <summary>Lê um sattr3.</summary>
    public static NfsSetAttributes ReadSetAttributes(XdrReader reader)
    {
        uint? mode = reader.Bool() ? reader.UInt32() : null;
        if (reader.Bool())
        {
            _ = reader.UInt32(); // uid
        }

        if (reader.Bool())
        {
            _ = reader.UInt32(); // gid
        }

        ulong? size = reader.Bool() ? reader.UInt64() : null;
        DateTime? modified = null;

        var atimeHow = reader.UInt32();
        if (atimeHow == 2)
        {
            _ = reader.UInt32();
            _ = reader.UInt32();
        }

        var mtimeHow = reader.UInt32();
        if (mtimeHow == 2)
        {
            var seconds = reader.UInt32();
            var nanoseconds = reader.UInt32();
            modified = DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanoseconds / 100);
        }
        else if (mtimeHow == 1)
        {
            modified = DateTime.UtcNow;
        }

        return new NfsSetAttributes(mode, size, modified);
    }
}
