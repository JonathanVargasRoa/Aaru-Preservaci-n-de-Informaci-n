// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : File.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Apple Macintosh File System plugin.
//
// --[ Description ] ----------------------------------------------------------
//
//     Methods to handle files.
//
// --[ License ] --------------------------------------------------------------
//
//     This library is free software; you can redistribute it and/or modify
//     it under the terms of the GNU Lesser General Public License as
//     published by the Free Software Foundation; either version 2.1 of the
//     License, or (at your option) any later version.
//
//     This library is distributed in the hope that it will be useful, but
//     WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//     Lesser General Public License for more details.
//
//     You should have received a copy of the GNU Lesser General Public
//     License along with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2018 Natalia Portillo
// ****************************************************************************/

using System;
using System.IO;
using DiscImageChef.Console;
using DiscImageChef.DiscImages;

namespace DiscImageChef.Filesystems.AppleMFS
{
    // Information from Inside Macintosh Volume II
    public partial class AppleMFS
    {
        public Errno MapBlock(string path, long fileBlock, out long deviceBlock)
        {
            deviceBlock = new long();

            if(!mounted) return Errno.AccessDenied;

            string[] pathElements = path.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
            if(pathElements.Length != 1) return Errno.NotSupported;

            if(!filenameToId.TryGetValue(path.ToLowerInvariant(), out uint fileId)) return Errno.NoSuchFile;

            if(!idToEntry.TryGetValue(fileId, out MFS_FileEntry entry)) return Errno.NoSuchFile;

            if(fileBlock > entry.flPyLen / volMDB.drAlBlkSiz) return Errno.InvalidArgument;

            uint nextBlock = entry.flStBlk;
            long relBlock = 0;

            while(true)
            {
                if(relBlock == fileBlock)
                {
                    deviceBlock = (nextBlock - 2) * sectorsPerBlock + volMDB.drAlBlSt + (long)partitionStart;
                    return Errno.NoError;
                }

                if(blockMap[nextBlock] == BMAP_FREE || blockMap[nextBlock] == BMAP_LAST) break;

                nextBlock = blockMap[nextBlock];
                relBlock++;
            }

            return Errno.InOutError;
        }

        public Errno GetAttributes(string path, out FileAttributes attributes)
        {
            attributes = new FileAttributes();
            if(!mounted) return Errno.AccessDenied;

            string[] pathElements = path.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
            if(pathElements.Length != 1) return Errno.NotSupported;

            if(!filenameToId.TryGetValue(path.ToLowerInvariant(), out uint fileId)) return Errno.NoSuchFile;

            if(!idToEntry.TryGetValue(fileId, out MFS_FileEntry entry)) return Errno.NoSuchFile;

            MFS_FinderFlags fdFlags = (MFS_FinderFlags)BigEndianBitConverter.ToUInt16(entry.flUsrWds, 0x08);

            if(fdFlags.HasFlag(MFS_FinderFlags.kIsAlias)) attributes |= FileAttributes.Alias;
            if(fdFlags.HasFlag(MFS_FinderFlags.kHasBundle)) attributes |= FileAttributes.Bundle;
            if(fdFlags.HasFlag(MFS_FinderFlags.kHasBeenInited)) attributes |= FileAttributes.HasBeenInited;
            if(fdFlags.HasFlag(MFS_FinderFlags.kHasCustomIcon)) attributes |= FileAttributes.HasCustomIcon;
            if(fdFlags.HasFlag(MFS_FinderFlags.kHasNoINITs)) attributes |= FileAttributes.HasNoINITs;
            if(fdFlags.HasFlag(MFS_FinderFlags.kIsInvisible)) attributes |= FileAttributes.Hidden;
            if(entry.flFlags.HasFlag(MFS_FileFlags.Locked)) attributes |= FileAttributes.Immutable;
            if(fdFlags.HasFlag(MFS_FinderFlags.kIsOnDesk)) attributes |= FileAttributes.IsOnDesk;
            if(fdFlags.HasFlag(MFS_FinderFlags.kIsShared)) attributes |= FileAttributes.Shared;
            if(fdFlags.HasFlag(MFS_FinderFlags.kIsStationery)) attributes |= FileAttributes.Stationery;

            if(!attributes.HasFlag(FileAttributes.Alias) && !attributes.HasFlag(FileAttributes.Bundle) &&
               !attributes.HasFlag(FileAttributes.Stationery)) attributes |= FileAttributes.File;

            attributes |= FileAttributes.BlockUnits;

            return Errno.NoError;
        }

        public Errno Read(string path, long offset, long size, ref byte[] buf)
        {
            if(!mounted) return Errno.AccessDenied;

            byte[] file;
            Errno error = Errno.NoError;

            if(debug && string.Compare(path, "$", StringComparison.InvariantCulture) == 0) file = directoryBlocks;
            else if(debug && string.Compare(path, "$Boot", StringComparison.InvariantCulture) == 0 && bootBlocks != null
            ) file = bootBlocks;
            else if(debug && string.Compare(path, "$Bitmap", StringComparison.InvariantCulture) == 0)
                file = blockMapBytes;
            else if(debug && string.Compare(path, "$MDB", StringComparison.InvariantCulture) == 0) file = mdbBlocks;
            else error = ReadFile(path, out file, false, false);

            if(error != Errno.NoError) return error;

            if(size == 0)
            {
                buf = new byte[0];
                return Errno.NoError;
            }

            if(offset >= file.Length) return Errno.InvalidArgument;

            if(size + offset >= file.Length) size = file.Length - offset;

            buf = new byte[size];

            Array.Copy(file, offset, buf, 0, size);

            return Errno.NoError;
        }

        public Errno Stat(string path, out FileEntryInfo stat)
        {
            stat = null;
            if(!mounted) return Errno.AccessDenied;

            string[] pathElements = path.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
            if(pathElements.Length != 1) return Errno.NotSupported;

            if(debug)
                if(string.Compare(path, "$", StringComparison.InvariantCulture) == 0 ||
                   string.Compare(path, "$Boot", StringComparison.InvariantCulture) == 0 ||
                   string.Compare(path, "$Bitmap", StringComparison.InvariantCulture) == 0 ||
                   string.Compare(path, "$MDB", StringComparison.InvariantCulture) == 0)
                {
                    stat = new FileEntryInfo
                    {
                        BlockSize = device.Info.SectorSize,
                        DeviceNo = 0,
                        GID = 0,
                        Inode = 0,
                        Links = 1,
                        Mode = 0x124,
                        UID = 0,
                        Attributes = FileAttributes.System
                    };

                    if(string.Compare(path, "$", StringComparison.InvariantCulture) == 0)
                    {
                        stat.Blocks = directoryBlocks.Length / stat.BlockSize + directoryBlocks.Length % stat.BlockSize;
                        stat.Length = directoryBlocks.Length;
                    }
                    else if(string.Compare(path, "$Bitmap", StringComparison.InvariantCulture) == 0)
                    {
                        stat.Blocks = blockMapBytes.Length / stat.BlockSize + blockMapBytes.Length % stat.BlockSize;
                        stat.Length = blockMapBytes.Length;
                    }
                    else if(string.Compare(path, "$Boot", StringComparison.InvariantCulture) == 0 && bootBlocks != null)
                    {
                        stat.Blocks = bootBlocks.Length / stat.BlockSize + bootBlocks.Length % stat.BlockSize;
                        stat.Length = bootBlocks.Length;
                    }
                    else if(string.Compare(path, "$MDB", StringComparison.InvariantCulture) == 0)
                    {
                        stat.Blocks = mdbBlocks.Length / stat.BlockSize + mdbBlocks.Length % stat.BlockSize;
                        stat.Length = mdbBlocks.Length;
                    }
                    else return Errno.InvalidArgument;

                    return Errno.NoError;
                }

            if(!filenameToId.TryGetValue(path.ToLowerInvariant(), out uint fileId)) return Errno.NoSuchFile;

            if(!idToEntry.TryGetValue(fileId, out MFS_FileEntry entry)) return Errno.NoSuchFile;

            Errno error = GetAttributes(path, out FileAttributes attr);
            if(error != Errno.NoError) return error;

            stat = new FileEntryInfo
            {
                Attributes = attr,
                Blocks = entry.flLgLen / volMDB.drAlBlkSiz,
                BlockSize = volMDB.drAlBlkSiz,
                CreationTime = DateHandlers.MacToDateTime(entry.flCrDat),
                DeviceNo = 0,
                GID = 0,
                Inode = entry.flFlNum,
                LastWriteTime = DateHandlers.MacToDateTime(entry.flMdDat),
                Length = entry.flPyLen,
                Links = 1,
                Mode = 0x124,
                UID = 0
            };

            return Errno.NoError;
        }

        public Errno ReadLink(string path, out string dest)
        {
            dest = null;
            return Errno.NotImplemented;
        }

        Errno ReadFile(string path, out byte[] buf, bool resourceFork, bool tags)
        {
            buf = null;

            if(!mounted) return Errno.AccessDenied;

            string[] pathElements = path.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
            if(pathElements.Length != 1) return Errno.NotSupported;

            if(!filenameToId.TryGetValue(path.ToLowerInvariant(), out uint fileId)) return Errno.NoSuchFile;

            if(!idToEntry.TryGetValue(fileId, out MFS_FileEntry entry)) return Errno.NoSuchFile;

            uint nextBlock;

            if(resourceFork)
            {
                if(entry.flRPyLen == 0)
                {
                    buf = new byte[0];
                    return Errno.NoError;
                }

                nextBlock = entry.flRStBlk;
            }
            else
            {
                if(entry.flPyLen == 0)
                {
                    buf = new byte[0];
                    return Errno.NoError;
                }

                nextBlock = entry.flStBlk;
            }

            MemoryStream ms = new MemoryStream();

            do
            {
                byte[] sectors;
                if(tags)
                    sectors =
                        device.ReadSectorsTag((ulong)((nextBlock - 2) * sectorsPerBlock) + volMDB.drAlBlSt + partitionStart,
                                              (uint)sectorsPerBlock, SectorTagType.AppleSectorTag);
                else
                    sectors =
                        device.ReadSectors((ulong)((nextBlock - 2) * sectorsPerBlock) + volMDB.drAlBlSt + partitionStart,
                                           (uint)sectorsPerBlock);

                ms.Write(sectors, 0, sectors.Length);

                if(blockMap[nextBlock] == BMAP_FREE)
                {
                    DicConsole.ErrorWriteLine("File truncated at block {0}", nextBlock);
                    break;
                }

                nextBlock = blockMap[nextBlock];
            }
            while(nextBlock > BMAP_LAST);

            if(tags) buf = ms.ToArray();
            else
            {
                if(resourceFork)
                    if(ms.Length < entry.flRLgLen) buf = ms.ToArray();
                    else
                    {
                        buf = new byte[entry.flRLgLen];
                        Array.Copy(ms.ToArray(), 0, buf, 0, buf.Length);
                    }
                else
                {
                    if(ms.Length < entry.flLgLen) buf = ms.ToArray();
                    else
                    {
                        buf = new byte[entry.flLgLen];
                        Array.Copy(ms.ToArray(), 0, buf, 0, buf.Length);
                    }
                }
            }

            return Errno.NoError;
        }
    }
}