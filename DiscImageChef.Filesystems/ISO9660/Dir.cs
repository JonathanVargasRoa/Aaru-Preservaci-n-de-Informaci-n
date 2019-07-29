using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DiscImageChef.CommonTypes.Structs;
using DiscImageChef.Helpers;

namespace DiscImageChef.Filesystems.ISO9660
{
    public partial class ISO9660
    {
        Dictionary<string, Dictionary<string, DecodedDirectoryEntry>> directoryCache;

        // TODO: Implement path table traversal
        public Errno ReadDir(string path, out List<string> contents)
        {
            contents = null;
            if(!mounted) return Errno.AccessDenied;

            if(string.IsNullOrWhiteSpace(path) || path == "/")
            {
                contents = GetFilenames(rootDirectoryCache);
                return Errno.NoError;
            }

            string cutPath = path.StartsWith("/", StringComparison.Ordinal)
                                 ? path.Substring(1).ToLower(CultureInfo.CurrentUICulture)
                                 : path.ToLower(CultureInfo.CurrentUICulture);

            if(directoryCache.TryGetValue(cutPath, out Dictionary<string, DecodedDirectoryEntry> currentDirectory))
            {
                contents = currentDirectory.Keys.ToList();
                return Errno.NoError;
            }

            string[] pieces = cutPath.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);

            KeyValuePair<string, DecodedDirectoryEntry> entry =
                rootDirectoryCache.FirstOrDefault(t => t.Key.ToLower(CultureInfo.CurrentUICulture) == pieces[0]);

            if(string.IsNullOrEmpty(entry.Key)) return Errno.NoSuchFile;

            if(!entry.Value.Flags.HasFlag(FileFlags.Directory)) return Errno.NotDirectory;

            string currentPath = pieces[0];

            currentDirectory = rootDirectoryCache;

            for(int p = 0; p < pieces.Length; p++)
            {
                entry = currentDirectory.FirstOrDefault(t => t.Key.ToLower(CultureInfo.CurrentUICulture) == pieces[p]);

                if(string.IsNullOrEmpty(entry.Key)) return Errno.NoSuchFile;

                if(!entry.Value.Flags.HasFlag(FileFlags.Directory)) return Errno.NotDirectory;

                currentPath = p == 0 ? pieces[0] : $"{currentPath}/{pieces[p]}";
                uint currentExtent = entry.Value.Extent;

                if(directoryCache.TryGetValue(currentPath, out currentDirectory)) continue;

                if(currentExtent == 0) return Errno.InvalidArgument;

                // TODO: XA, High Sierra
                uint dirSizeInSectors = entry.Value.Size / 2048;
                if(entry.Value.Size % 2048 > 0) dirSizeInSectors++;

                byte[] directoryBuffer = image.ReadSectors(currentExtent, dirSizeInSectors);

                // TODO: Decode Joliet
                currentDirectory = cdi
                                       ? DecodeCdiDirectory(directoryBuffer)
                                       : highSierra
                                           ? DecodeHighSierraDirectory(directoryBuffer)
                                           : DecodeIsoDirectory(directoryBuffer);

                if(usePathTable)
                    foreach(DecodedDirectoryEntry subDirectory in cdi
                                                                      ? GetSubdirsFromCdiPathTable(currentPath)
                                                                      : highSierra
                                                                          ? GetSubdirsFromHighSierraPathTable(currentPath)
                                                                          : GetSubdirsFromIsoPathTable(currentPath))
                        currentDirectory[subDirectory.Filename] = subDirectory;

                directoryCache.Add(currentPath, currentDirectory);
            }

            contents = GetFilenames(currentDirectory);
            return Errno.NoError;
        }

        List<string> GetFilenames(Dictionary<string, DecodedDirectoryEntry> dirents)
        {
            List<string> contents = new List<string>();
            foreach(DecodedDirectoryEntry entry in dirents.Values)
                switch(@namespace)
                {
                    case Namespace.Normal:
                        contents.Add(entry.Filename.EndsWith(";1", StringComparison.Ordinal)
                                         ? entry.Filename.Substring(0, entry.Filename.Length - 2)
                                         : entry.Filename);

                        break;
                    case Namespace.Vms:
                    case Namespace.Joliet:
                    case Namespace.Rrip:
                    case Namespace.Romeo:
                        contents.Add(entry.Filename);
                        break;
                    default: throw new ArgumentOutOfRangeException();
                }

            return contents;
        }

        Dictionary<string, DecodedDirectoryEntry> DecodeCdiDirectory(byte[] data) =>
            throw new NotImplementedException();

        Dictionary<string, DecodedDirectoryEntry> DecodeHighSierraDirectory(byte[] data)
        {
            Dictionary<string, DecodedDirectoryEntry> entries  = new Dictionary<string, DecodedDirectoryEntry>();
            int                                       entryOff = 0;

            while(entryOff + DirectoryRecordSize < data.Length)
            {
                HighSierraDirectoryRecord record =
                    Marshal.ByteArrayToStructureLittleEndian<HighSierraDirectoryRecord>(data, entryOff,
                                                                                        Marshal
                                                                                           .SizeOf<DirectoryRecord>());

                if(record.length == 0) break;

                // Special entries for current and parent directories, skip them
                if(record.name_len == 1)
                    if(data[entryOff + DirectoryRecordSize] == 0 || data[entryOff + DirectoryRecordSize] == 1)
                    {
                        entryOff += record.length;
                        continue;
                    }

                DecodedDirectoryEntry entry = new DecodedDirectoryEntry
                {
                    Extent               = record.size == 0 ? 0 : record.extent,
                    Size                 = record.size,
                    Flags                = record.flags,
                    Interleave           = record.interleave,
                    VolumeSequenceNumber = record.volume_sequence_number,
                    Filename             = Encoding.GetString(data, entryOff + DirectoryRecordSize, record.name_len),
                    Timestamp            = DecodeHighSierraDateTime(record.date)
                };

                if(entry.Flags.HasFlag(FileFlags.Directory) && usePathTable)
                {
                    entryOff += record.length;
                    continue;
                }

                if(!entries.ContainsKey(entry.Filename)) entries.Add(entry.Filename, entry);

                entryOff += record.length;
            }

            return entries;
        }

        Dictionary<string, DecodedDirectoryEntry> DecodeIsoDirectory(byte[] data)
        {
            Dictionary<string, DecodedDirectoryEntry> entries  = new Dictionary<string, DecodedDirectoryEntry>();
            int                                       entryOff = 0;

            while(entryOff + DirectoryRecordSize < data.Length)
            {
                DirectoryRecord record =
                    Marshal.ByteArrayToStructureLittleEndian<DirectoryRecord>(data, entryOff,
                                                                              Marshal.SizeOf<DirectoryRecord>());

                if(record.length == 0) break;

                // Special entries for current and parent directories, skip them
                if(record.name_len == 1)
                    if(data[entryOff + DirectoryRecordSize] == 0 || data[entryOff + DirectoryRecordSize] == 1)
                    {
                        entryOff += record.length;
                        continue;
                    }

                DecodedDirectoryEntry entry = new DecodedDirectoryEntry
                {
                    Extent = record.size == 0 ? 0 : record.extent,
                    Size   = record.size,
                    Flags  = record.flags,
                    Filename =
                        joliet
                            ? Encoding.BigEndianUnicode.GetString(data, entryOff + DirectoryRecordSize,
                                                                  record.name_len)
                            : Encoding.GetString(data, entryOff + DirectoryRecordSize, record.name_len),
                    FileUnitSize         = record.file_unit_size,
                    Interleave           = record.interleave,
                    VolumeSequenceNumber = record.volume_sequence_number,
                    Timestamp            = DecodeIsoDateTime(record.date)
                };

                if(entry.Flags.HasFlag(FileFlags.Directory) && usePathTable)
                {
                    entryOff += record.length;
                    continue;
                }

                // Mac OS can use slashes, we cannot
                entry.Filename = entry.Filename.Replace('/', '\u2215');

                // Tailing '.' is only allowed on RRIP. If present it will be recreated below with the alternate name
                if(entry.Filename.EndsWith(".", StringComparison.Ordinal))
                    entry.Filename = entry.Filename.Substring(0, entry.Filename.Length - 1);

                if(entry.Filename.EndsWith(".;1", StringComparison.Ordinal))
                    entry.Filename = entry.Filename.Substring(0, entry.Filename.Length - 3) + ";1";

                // This is a legal Joliet name, different from VMS version fields, but Nero MAX incorrectly creates these filenames
                if(joliet && entry.Filename.EndsWith(";1", StringComparison.Ordinal))
                    entry.Filename = entry.Filename.Substring(0, entry.Filename.Length - 2);

                // TODO: XA
                int systemAreaStart  = entryOff + record.name_len      + Marshal.SizeOf<DirectoryRecord>();
                int systemAreaLength = record.length - record.name_len - Marshal.SizeOf<DirectoryRecord>();

                if(systemAreaStart % 2 != 0)
                {
                    systemAreaStart++;
                    systemAreaLength--;
                }

                DecodeSystemArea(data, systemAreaStart, systemAreaStart + systemAreaLength, ref entry,
                                 out bool hasResourceFork);

                // TODO: Multi-extent files
                if(entry.Flags.HasFlag(FileFlags.Associated))
                {
                    if(entries.ContainsKey(entry.Filename))
                    {
                        if(hasResourceFork) entries[entry.Filename].ResourceFork = entry;
                        else entries[entry.Filename].AssociatedFile              = entry;
                    }
                    else
                    {
                        entries[entry.Filename] = new DecodedDirectoryEntry
                        {
                            Extent               = 0,
                            Size                 = 0,
                            Flags                = record.flags ^ FileFlags.Associated,
                            FileUnitSize         = 0,
                            Interleave           = 0,
                            VolumeSequenceNumber = record.volume_sequence_number,
                            Filename             = entry.Filename,
                            Timestamp            = DecodeIsoDateTime(record.date)
                        };

                        if(hasResourceFork) entries[entry.Filename].ResourceFork = entry;
                        else entries[entry.Filename].AssociatedFile              = entry;
                    }
                }
                else
                {
                    if(entries.ContainsKey(entry.Filename))
                    {
                        entry.AssociatedFile = entries[entry.Filename].AssociatedFile;
                        entry.ResourceFork   = entries[entry.Filename].ResourceFork;
                    }

                    entries[entry.Filename] = entry;
                }

                entryOff += record.length;
            }

            // Relocated directories should be shown in correct place when using Rock Ridge namespace
            return @namespace == Namespace.Rrip
                       ? entries.Where(e => !e.Value.RockRidgeRelocated).ToDictionary(x => x.Key, x => x.Value)
                       : entries;
        }

        void DecodeSystemArea(byte[]   data, int start, int end, ref DecodedDirectoryEntry entry,
                              out bool hasResourceFork)
        {
            int systemAreaOff = start;
            hasResourceFork = false;
            bool continueSymlink          = false;
            bool continueSymlinkComponent = false;

            while(systemAreaOff + 2 <= end)
            {
                ushort systemAreaSignature = BigEndianBitConverter.ToUInt16(data, systemAreaOff);

                if(BigEndianBitConverter.ToUInt16(data, systemAreaOff + 6) == XA_MAGIC) systemAreaSignature = XA_MAGIC;

                switch(systemAreaSignature)
                {
                    case APPLE_MAGIC:
                        byte    appleLength = data[systemAreaOff + 2];
                        AppleId appleId     = (AppleId)data[systemAreaOff + 3];

                        // Old AAIP
                        if(appleId == AppleId.ProDOS && appleLength != 7) goto case AAIP_MAGIC;

                        switch(appleId)
                        {
                            case AppleId.ProDOS:
                                AppleProDOSSystemUse appleProDosSystemUse =
                                    Marshal.ByteArrayToStructureLittleEndian<AppleProDOSSystemUse>(data, systemAreaOff,
                                                                                                   Marshal
                                                                                                      .SizeOf<
                                                                                                           AppleProDOSSystemUse
                                                                                                       >());

                                entry.AppleProDosType = appleProDosSystemUse.aux_type;
                                entry.AppleDosType    = appleProDosSystemUse.type;

                                break;
                            case AppleId.HFS:
                                AppleHFSSystemUse appleHfsSystemUse =
                                    Marshal.ByteArrayToStructureBigEndian<AppleHFSSystemUse>(data, systemAreaOff,
                                                                                             Marshal
                                                                                                .SizeOf<
                                                                                                     AppleHFSSystemUse
                                                                                                 >());

                                hasResourceFork = true;

                                entry.FinderInfo           = new FinderInfo();
                                entry.FinderInfo.fdCreator = appleHfsSystemUse.creator;
                                entry.FinderInfo.fdFlags   = (FinderFlags)appleHfsSystemUse.finder_flags;
                                entry.FinderInfo.fdType    = appleHfsSystemUse.type;

                                break;
                        }

                        systemAreaOff += appleLength;
                        break;
                    case APPLE_MAGIC_OLD:
                        AppleOldId appleOldId = (AppleOldId)data[systemAreaOff + 2];

                        switch(appleOldId)
                        {
                            case AppleOldId.ProDOS:
                                AppleProDOSOldSystemUse appleProDosOldSystemUse =
                                    Marshal.ByteArrayToStructureLittleEndian<AppleProDOSOldSystemUse>(data,
                                                                                                      systemAreaOff,
                                                                                                      Marshal
                                                                                                         .SizeOf<
                                                                                                              AppleProDOSOldSystemUse
                                                                                                          >());
                                entry.AppleProDosType = appleProDosOldSystemUse.aux_type;
                                entry.AppleDosType    = appleProDosOldSystemUse.type;

                                systemAreaOff += Marshal.SizeOf<AppleProDOSOldSystemUse>();
                                break;
                            case AppleOldId.TypeCreator:
                            case AppleOldId.TypeCreatorBundle:
                                AppleHFSTypeCreatorSystemUse appleHfsTypeCreatorSystemUse =
                                    Marshal.ByteArrayToStructureBigEndian<AppleHFSTypeCreatorSystemUse>(data,
                                                                                                        systemAreaOff,
                                                                                                        Marshal
                                                                                                           .SizeOf<
                                                                                                                AppleHFSTypeCreatorSystemUse
                                                                                                            >());

                                hasResourceFork = true;

                                entry.FinderInfo           = new FinderInfo();
                                entry.FinderInfo.fdCreator = appleHfsTypeCreatorSystemUse.creator;
                                entry.FinderInfo.fdType    = appleHfsTypeCreatorSystemUse.type;

                                systemAreaOff += Marshal.SizeOf<AppleHFSTypeCreatorSystemUse>();
                                break;
                            case AppleOldId.TypeCreatorIcon:
                            case AppleOldId.TypeCreatorIconBundle:
                                AppleHFSIconSystemUse appleHfsIconSystemUse =
                                    Marshal.ByteArrayToStructureBigEndian<AppleHFSIconSystemUse>(data, systemAreaOff,
                                                                                                 Marshal
                                                                                                    .SizeOf<
                                                                                                         AppleHFSIconSystemUse
                                                                                                     >());

                                hasResourceFork = true;

                                entry.FinderInfo           = new FinderInfo();
                                entry.FinderInfo.fdCreator = appleHfsIconSystemUse.creator;
                                entry.FinderInfo.fdType    = appleHfsIconSystemUse.type;
                                entry.AppleIcon            = appleHfsIconSystemUse.icon;

                                systemAreaOff += Marshal.SizeOf<AppleHFSIconSystemUse>();
                                break;
                            case AppleOldId.HFS:
                                AppleHFSOldSystemUse appleHfsSystemUse =
                                    Marshal.ByteArrayToStructureBigEndian<AppleHFSOldSystemUse>(data, systemAreaOff,
                                                                                                Marshal
                                                                                                   .SizeOf<
                                                                                                        AppleHFSOldSystemUse
                                                                                                    >());

                                hasResourceFork = true;

                                entry.FinderInfo           = new FinderInfo();
                                entry.FinderInfo.fdCreator = appleHfsSystemUse.creator;
                                entry.FinderInfo.fdFlags   = (FinderFlags)appleHfsSystemUse.finder_flags;
                                entry.FinderInfo.fdType    = appleHfsSystemUse.type;

                                systemAreaOff += Marshal.SizeOf<AppleHFSOldSystemUse>();
                                break;
                            default:
                                // Cannot continue as we don't know this structure size
                                systemAreaOff = end;
                                break;
                        }

                        break;
                    case XA_MAGIC:
                        entry.XA = Marshal.ByteArrayToStructureBigEndian<CdromXa>(data, systemAreaOff,
                                                                                  Marshal.SizeOf<CdromXa>());

                        systemAreaOff += Marshal.SizeOf<CdromXa>();
                        break;
                    // All of these follow the SUSP indication of 2 bytes for signature 1 byte for length
                    case AAIP_MAGIC:
                    case AMIGA_MAGIC:
                        AmigaEntry amiga =
                            Marshal.ByteArrayToStructureBigEndian<AmigaEntry>(data, systemAreaOff,
                                                                              Marshal.SizeOf<AmigaEntry>());

                        int protectionLength = 0;

                        if(amiga.flags.HasFlag(AmigaFlags.Protection))
                        {
                            entry.AmigaProtection =
                                Marshal.ByteArrayToStructureBigEndian<AmigaProtection>(data,
                                                                                       systemAreaOff +
                                                                                       Marshal.SizeOf<AmigaEntry>(),
                                                                                       Marshal
                                                                                          .SizeOf<AmigaProtection>());

                            protectionLength = Marshal.SizeOf<AmigaProtection>();
                        }

                        if(amiga.flags.HasFlag(AmigaFlags.Comment))
                        {
                            if(entry.AmigaComment is null) entry.AmigaComment = new byte[0];

                            byte[] newComment = new byte[entry.AmigaComment.Length +
                                                         data
                                                             [systemAreaOff + Marshal.SizeOf<AmigaEntry>() + protectionLength] -
                                                         1];

                            Array.Copy(entry.AmigaComment, 0, newComment, 0, entry.AmigaComment.Length);

                            Array.Copy(data, systemAreaOff + Marshal.SizeOf<AmigaEntry>() + protectionLength,
                                       newComment, entry.AmigaComment.Length,
                                       data[systemAreaOff + Marshal.SizeOf<AmigaEntry>() + protectionLength] - 1);

                            entry.AmigaComment = newComment;
                        }

                        systemAreaOff += amiga.length;
                        break;
                    // This merely indicates the existence of RRIP extensions, we don't need it
                    case RRIP_MAGIC:
                        byte rripLength = data[systemAreaOff + 2];
                        systemAreaOff += rripLength;

                        break;
                    case RRIP_POSIX_ATTRIBUTES:
                        byte pxLength = data[systemAreaOff + 2];

                        if(pxLength == 36)
                            entry.PosixAttributesOld =
                                Marshal.ByteArrayToStructureLittleEndian<PosixAttributesOld>(data, systemAreaOff,
                                                                                             Marshal
                                                                                                .SizeOf<
                                                                                                     PosixAttributesOld
                                                                                                 >());
                        else if(pxLength >= 44)
                            entry.PosixAttributes =
                                Marshal.ByteArrayToStructureLittleEndian<PosixAttributes>(data, systemAreaOff,
                                                                                          Marshal
                                                                                             .SizeOf<PosixAttributes
                                                                                              >());

                        systemAreaOff += pxLength;
                        break;
                    case RRIP_POSIX_DEV_NO:
                        byte pnLength = data[systemAreaOff + 2];

                        entry.PosixDeviceNumber =
                            Marshal.ByteArrayToStructureLittleEndian<PosixDeviceNumber>(data, systemAreaOff,
                                                                                        Marshal
                                                                                           .SizeOf<PosixDeviceNumber
                                                                                            >());
                        systemAreaOff += pnLength;
                        break;
                    case RRIP_SYMLINK:
                        byte slLength = data[systemAreaOff + 2];

                        SymbolicLink sl =
                            Marshal.ByteArrayToStructureLittleEndian<SymbolicLink>(data, systemAreaOff,
                                                                                   Marshal.SizeOf<SymbolicLink>());

                        SymbolicLinkComponent slc =
                            Marshal.ByteArrayToStructureLittleEndian<SymbolicLinkComponent>(data,
                                                                                            systemAreaOff +
                                                                                            Marshal
                                                                                               .SizeOf<SymbolicLink>(),
                                                                                            Marshal
                                                                                               .SizeOf<
                                                                                                    SymbolicLinkComponent
                                                                                                >());

                        if(!continueSymlink || entry.SymbolicLink is null) entry.SymbolicLink = "";

                        if(slc.flags.HasFlag(SymlinkComponentFlags.Root)) entry.SymbolicLink    =  "/";
                        if(slc.flags.HasFlag(SymlinkComponentFlags.Current)) entry.SymbolicLink += ".";
                        if(slc.flags.HasFlag(SymlinkComponentFlags.Parent)) entry.SymbolicLink  += "..";

                        if(!continueSymlinkComponent && !slc.flags.HasFlag(SymlinkComponentFlags.Root))
                            entry.SymbolicLink += "/";

                        entry.SymbolicLink += slc.flags.HasFlag(SymlinkComponentFlags.Networkname)
                                                  ? Environment.MachineName
                                                  : joliet
                                                      ? Encoding.BigEndianUnicode.GetString(data,
                                                                                            systemAreaOff +
                                                                                            Marshal
                                                                                               .SizeOf<SymbolicLink>() +
                                                                                            Marshal
                                                                                               .SizeOf<
                                                                                                    SymbolicLinkComponent
                                                                                                >(), slc.length)
                                                      : Encoding.GetString(data,
                                                                           systemAreaOff                  +
                                                                           Marshal.SizeOf<SymbolicLink>() +
                                                                           Marshal.SizeOf<SymbolicLinkComponent>(),
                                                                           slc.length);

                        continueSymlink          = sl.flags.HasFlag(SymlinkFlags.Continue);
                        continueSymlinkComponent = slc.flags.HasFlag(SymlinkComponentFlags.Continue);

                        systemAreaOff += slLength;
                        break;
                    case RRIP_NAME:
                        byte nmLength = data[systemAreaOff + 2];

                        if(@namespace != Namespace.Rrip)
                        {
                            systemAreaOff += nmLength;
                            break;
                        }

                        AlternateName alternateName =
                            Marshal.ByteArrayToStructureLittleEndian<AlternateName>(data, systemAreaOff,
                                                                                    Marshal.SizeOf<AlternateName>());

                        byte[] nm;
                        if(alternateName.flags.HasFlag(AlternateNameFlags.Networkname))
                            nm = joliet
                                     ? Encoding.BigEndianUnicode.GetBytes(Environment.MachineName)
                                     : Encoding.GetBytes(Environment.MachineName);
                        else
                        {
                            nm = new byte[nmLength - Marshal.SizeOf<AlternateName>()];

                            Array.Copy(data, systemAreaOff + Marshal.SizeOf<AlternateName>(), nm, 0, nm.Length);
                        }

                        if(entry.RockRidgeAlternateName is null) entry.RockRidgeAlternateName = new byte[0];

                        byte[] newNm = new byte[entry.RockRidgeAlternateName.Length + nm.Length];
                        Array.Copy(entry.RockRidgeAlternateName, 0, newNm, 0,
                                   entry.RockRidgeAlternateName.Length);
                        Array.Copy(nm, 0, newNm, entry.RockRidgeAlternateName.Length,
                                   nm.Length);

                        entry.RockRidgeAlternateName = newNm;

                        if(!alternateName.flags.HasFlag(AlternateNameFlags.Continue))
                        {
                            entry.Filename = joliet
                                                 ? Encoding.BigEndianUnicode.GetString(entry.RockRidgeAlternateName)
                                                 : Encoding.GetString(entry.RockRidgeAlternateName);
                            entry.RockRidgeAlternateName = null;
                        }

                        systemAreaOff += nmLength;
                        break;
                    case RRIP_CHILDLINK:
                        // TODO
                        byte clLength = data[systemAreaOff + 2];

                        // If we are not in Rock Ridge namespace, or we are using the Path Table, skip it
                        if(@namespace != Namespace.Rrip || usePathTable)
                        {
                            systemAreaOff += clLength;
                            break;
                        }

                        ChildLink cl =
                            Marshal.ByteArrayToStructureLittleEndian<ChildLink>(data, systemAreaOff,
                                                                                Marshal.SizeOf<ChildLink>());

                        byte[] childSector = image.ReadSector(cl.child_dir_lba);
                        DirectoryRecord childRecord =
                            Marshal.ByteArrayToStructureLittleEndian<DirectoryRecord>(childSector);

                        // As per RRIP 4.1.5.1, we leave name as in previous entry, substitute location with the one in
                        // the CL, and replace all other fields with the ones found in the first entry of the child
                        entry.Extent               = cl.child_dir_lba;
                        entry.Size                 = childRecord.size;
                        entry.Flags                = childRecord.flags;
                        entry.FileUnitSize         = childRecord.file_unit_size;
                        entry.Interleave           = childRecord.interleave;
                        entry.VolumeSequenceNumber = childRecord.volume_sequence_number;
                        entry.Timestamp            = DecodeIsoDateTime(childRecord.date);

                        systemAreaOff += clLength;

                        break;
                    case RRIP_PARENTLINK:
                        // SKip, we don't need it
                        byte plLength = data[systemAreaOff + 2];
                        systemAreaOff += plLength;

                        break;
                    case RRIP_RELOCATED_DIR:
                        byte reLength = data[systemAreaOff + 2];
                        systemAreaOff += reLength;

                        entry.RockRidgeRelocated = true;

                        break;
                    case RRIP_TIMESTAMPS:
                        byte tfLength = data[systemAreaOff + 2];

                        Timestamps timestamps =
                            Marshal.ByteArrayToStructureLittleEndian<Timestamps>(data, systemAreaOff,
                                                                                 Marshal.SizeOf<Timestamps>());

                        int tfOff = systemAreaOff + Marshal.SizeOf<Timestamps>();
                        int tfLen = timestamps.flags.HasFlag(TimestampFlags.LongFormat) ? 17 : 7;

                        if(timestamps.flags.HasFlag(TimestampFlags.Creation))
                        {
                            entry.RripCreation = new byte[tfLen];
                            Array.Copy(data, tfOff, entry.RripCreation, 0, tfLen);
                            tfOff += tfLen;
                        }

                        if(timestamps.flags.HasFlag(TimestampFlags.Modification))
                        {
                            entry.RripModify = new byte[tfLen];
                            Array.Copy(data, tfOff, entry.RripModify, 0, tfLen);
                            tfOff += tfLen;
                        }

                        if(timestamps.flags.HasFlag(TimestampFlags.Access))
                        {
                            entry.RripAccess = new byte[tfLen];
                            Array.Copy(data, tfOff, entry.RripAccess, 0, tfLen);
                            tfOff += tfLen;
                        }

                        if(timestamps.flags.HasFlag(TimestampFlags.AttributeChange))
                        {
                            entry.RripAttributeChange = new byte[tfLen];
                            Array.Copy(data, tfOff, entry.RripAttributeChange, 0, tfLen);
                            tfOff += tfLen;
                        }

                        if(timestamps.flags.HasFlag(TimestampFlags.Backup))
                        {
                            entry.RripBackup = new byte[tfLen];
                            Array.Copy(data, tfOff, entry.RripBackup, 0, tfLen);
                            tfOff += tfLen;
                        }

                        if(timestamps.flags.HasFlag(TimestampFlags.Expiration))
                        {
                            entry.RripExpiration = new byte[tfLen];
                            Array.Copy(data, tfOff, entry.RripExpiration, 0, tfLen);
                            tfOff += tfLen;
                        }

                        if(timestamps.flags.HasFlag(TimestampFlags.Effective))
                        {
                            entry.RripEffective = new byte[tfLen];
                            Array.Copy(data, tfOff, entry.RripEffective, 0, tfLen);
                        }

                        systemAreaOff += tfLength;
                        break;
                    case RRIP_SPARSE:
                        // TODO
                        byte sfLength = data[systemAreaOff + 2];
                        systemAreaOff += sfLength;

                        break;
                    case SUSP_CONTINUATION:
                        byte ceLength = data[systemAreaOff + 2];

                        ContinuationArea ca =
                            Marshal.ByteArrayToStructureLittleEndian<ContinuationArea>(data, systemAreaOff,
                                                                                       Marshal
                                                                                          .SizeOf<ContinuationArea>());

                        uint caOffSector    = ca.offset    / 2048;
                        uint caOff          = ca.offset    % 2048;
                        uint caLenInSectors = ca.ca_length / 2048;
                        if((ca.ca_length + caOff) % 2048 > 0) caLenInSectors++;

                        byte[] caData = image.ReadSectors(ca.block + caOffSector, caLenInSectors);

                        DecodeSystemArea(caData, (int)caOff, (int)(caOff + ca.ca_length), ref entry,
                                         out hasResourceFork);

                        systemAreaOff += ceLength;
                        break;
                    case SUSP_PADDING:
                        // Just padding, skip
                        byte pdLength = data[systemAreaOff + 2];
                        systemAreaOff += pdLength;

                        break;
                    case SUSP_INDICATOR:
                        // Only to be found on CURRENT entry of root directory
                        byte spLength = data[systemAreaOff + 2];
                        systemAreaOff += spLength;

                        break;
                    case SUSP_TERMINATOR:
                        // Not seen on the wild
                        byte stLength = data[systemAreaOff + 2];
                        systemAreaOff += stLength;

                        break;
                    case SUSP_REFERENCE:
                        // Only to be found on CURRENT entry of root directory
                        byte erLength = data[systemAreaOff + 2];
                        systemAreaOff += erLength;

                        break;
                    case SUSP_SELECTOR:
                        // Only to be found on CURRENT entry of root directory
                        byte esLength = data[systemAreaOff + 2];
                        systemAreaOff += esLength;

                        break;
                    case ZISO_MAGIC:
                        // TODO: Implement support for zisofs
                        byte zfLength = data[systemAreaOff + 2];
                        systemAreaOff += zfLength;

                        break;
                    default:
                        // Cannot continue as we don't know this structure size
                        systemAreaOff = end;
                        break;
                }
            }
        }

        PathTableEntryInternal[] GetPathTableEntries(string path)
        {
            IEnumerable<PathTableEntryInternal> tableEntries  = new PathTableEntryInternal[0];
            List<PathTableEntryInternal>        pathTableList = new List<PathTableEntryInternal>(pathTable);

            if(path == "" || path == "/") tableEntries = pathTable.Where(p => p.Parent == 1 && p != pathTable[0]);
            else
            {
                string cutPath = path.StartsWith("/", StringComparison.Ordinal)
                                     ? path.Substring(1).ToLower(CultureInfo.CurrentUICulture)
                                     : path.ToLower(CultureInfo.CurrentUICulture);

                string[] pieces = cutPath.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);

                int currentParent = 1;
                int currentPiece  = 0;

                while(currentPiece < pieces.Length)
                {
                    PathTableEntryInternal currentEntry = pathTable.FirstOrDefault(p => p.Parent == currentParent &&
                                                                                        p.Name.ToLower(CultureInfo
                                                                                                          .CurrentUICulture) ==
                                                                                        pieces[currentPiece]);

                    if(currentEntry is null) break;

                    currentPiece++;
                    currentParent = pathTableList.IndexOf(currentEntry) + 1;
                }

                tableEntries = pathTable.Where(p => p.Parent == currentParent);
            }

            return tableEntries.ToArray();
        }

        DecodedDirectoryEntry[] GetSubdirsFromCdiPathTable(string path) => throw new NotImplementedException();

        DecodedDirectoryEntry[] GetSubdirsFromIsoPathTable(string path)
        {
            PathTableEntryInternal[]    tableEntries = GetPathTableEntries(path);
            List<DecodedDirectoryEntry> entries      = new List<DecodedDirectoryEntry>();
            foreach(PathTableEntryInternal tEntry in tableEntries)
            {
                byte[] sector = image.ReadSector(tEntry.Extent);
                DirectoryRecord record =
                    Marshal.ByteArrayToStructureLittleEndian<DirectoryRecord>(sector, 0,
                                                                              Marshal.SizeOf<DirectoryRecord>());

                if(record.length == 0) break;

                DecodedDirectoryEntry entry = new DecodedDirectoryEntry
                {
                    Extent               = record.size == 0 ? 0 : record.extent,
                    Size                 = record.size,
                    Flags                = record.flags,
                    Filename             = tEntry.Name,
                    FileUnitSize         = record.file_unit_size,
                    Interleave           = record.interleave,
                    VolumeSequenceNumber = record.volume_sequence_number,
                    Timestamp            = DecodeIsoDateTime(record.date)
                };

                // TODO: XA
                int systemAreaStart  = record.name_len                 + Marshal.SizeOf<DirectoryRecord>();
                int systemAreaLength = record.length - record.name_len - Marshal.SizeOf<DirectoryRecord>();

                if(systemAreaStart % 2 != 0)
                {
                    systemAreaStart++;
                    systemAreaLength--;
                }

                DecodeSystemArea(sector, systemAreaStart, systemAreaStart + systemAreaLength, ref entry, out _);

                entries.Add(entry);
            }

            return entries.ToArray();
        }

        DecodedDirectoryEntry[] GetSubdirsFromHighSierraPathTable(string path)
        {
            PathTableEntryInternal[]    tableEntries = GetPathTableEntries(path);
            List<DecodedDirectoryEntry> entries      = new List<DecodedDirectoryEntry>();
            foreach(PathTableEntryInternal tEntry in tableEntries)
            {
                byte[] sector = image.ReadSector(tEntry.Extent);
                HighSierraDirectoryRecord record =
                    Marshal.ByteArrayToStructureLittleEndian<HighSierraDirectoryRecord>(sector, 0,
                                                                                        Marshal
                                                                                           .SizeOf<
                                                                                                HighSierraDirectoryRecord
                                                                                            >());

                DecodedDirectoryEntry entry = new DecodedDirectoryEntry
                {
                    Extent               = record.size == 0 ? 0 : record.extent,
                    Size                 = record.size,
                    Flags                = record.flags,
                    Filename             = tEntry.Name,
                    Interleave           = record.interleave,
                    VolumeSequenceNumber = record.volume_sequence_number,
                    Timestamp            = DecodeHighSierraDateTime(record.date)
                };

                entries.Add(entry);
            }

            return entries.ToArray();
        }
    }
}