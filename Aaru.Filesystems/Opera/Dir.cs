// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : Dir.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Opera filesystem plugin.
//
// --[ Description ] ----------------------------------------------------------
//
//     Methods to handle Opera filesystem directories.
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
// Copyright © 2011-2020 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Aaru.CommonTypes.Structs;
using Aaru.Helpers;

namespace Aaru.Filesystems
{
    public partial class OperaFS
    {
        public Errno ReadDir(string path, out List<string> contents)
        {
            contents = null;

            if(!mounted)
                return Errno.AccessDenied;

            if(string.IsNullOrWhiteSpace(path) ||
               path == "/")
            {
                contents = rootDirectoryCache.Keys.ToList();

                return Errno.NoError;
            }

            string cutPath = path.StartsWith("/", StringComparison.Ordinal)
                                 ? path.Substring(1).ToLower(CultureInfo.CurrentUICulture)
                                 : path.ToLower(CultureInfo.CurrentUICulture);

            if(directoryCache.TryGetValue(cutPath, out Dictionary<string, DirectoryEntryWithPointers> currentDirectory))
            {
                contents = currentDirectory.Keys.ToList();

                return Errno.NoError;
            }

            string[] pieces = cutPath.Split(new[]
            {
                '/'
            }, StringSplitOptions.RemoveEmptyEntries);

            KeyValuePair<string, DirectoryEntryWithPointers> entry =
                rootDirectoryCache.FirstOrDefault(t => t.Key.ToLower(CultureInfo.CurrentUICulture) == pieces[0]);

            if(string.IsNullOrEmpty(entry.Key))
                return Errno.NoSuchFile;

            if((entry.Value.entry.flags & FLAGS_MASK) != (int)FileFlags.Directory)
                return Errno.NotDirectory;

            string currentPath = pieces[0];

            currentDirectory = rootDirectoryCache;

            for(int p = 0; p < pieces.Length; p++)
            {
                entry = currentDirectory.FirstOrDefault(t => t.Key.ToLower(CultureInfo.CurrentUICulture) == pieces[p]);

                if(string.IsNullOrEmpty(entry.Key))
                    return Errno.NoSuchFile;

                if((entry.Value.entry.flags & FLAGS_MASK) != (int)FileFlags.Directory)
                    return Errno.NotDirectory;

                currentPath = p == 0 ? pieces[0] : $"{currentPath}/{pieces[p]}";

                if(directoryCache.TryGetValue(currentPath, out currentDirectory))
                    continue;

                if(entry.Value.pointers.Length < 1)
                    return Errno.InvalidArgument;

                currentDirectory = DecodeDirectory((int)entry.Value.pointers[0]);

                directoryCache.Add(currentPath, currentDirectory);
            }

            contents = currentDirectory?.Keys.ToList();

            return Errno.NoError;
        }

        Dictionary<string, DirectoryEntryWithPointers> DecodeDirectory(int firstBlock)
        {
            Dictionary<string, DirectoryEntryWithPointers> entries =
                new Dictionary<string, DirectoryEntryWithPointers>();

            int nextBlock = firstBlock;

            DirectoryHeader header;

            do
            {
                byte[] data = image.ReadSectors((ulong)(nextBlock * volumeBlockSizeRatio), volumeBlockSizeRatio);
                header    = Marshal.ByteArrayToStructureBigEndian<DirectoryHeader>(data);
                nextBlock = header.next_block + firstBlock;

                int off = (int)header.first_used;

                var entry = new DirectoryEntry();

                while(off + DirectoryEntrySize < data.Length)
                {
                    entry = Marshal.ByteArrayToStructureBigEndian<DirectoryEntry>(data, off, DirectoryEntrySize);
                    string name = StringHandlers.CToString(entry.name, Encoding);

                    var entryWithPointers = new DirectoryEntryWithPointers
                    {
                        entry    = entry,
                        pointers = new uint[entry.last_copy + 1]
                    };

                    for(int i = 0; i <= entry.last_copy; i++)
                        entryWithPointers.pointers[i] =
                            BigEndianBitConverter.ToUInt32(data, off + DirectoryEntrySize + (i * 4));

                    entries.Add(name, entryWithPointers);

                    if((entry.flags & (uint)FileFlags.LastEntry)        != 0 ||
                       (entry.flags & (uint)FileFlags.LastEntryInBlock) != 0)
                        break;

                    off += (int)(DirectoryEntrySize + ((entry.last_copy + 1) * 4));
                }

                if((entry.flags & (uint)FileFlags.LastEntry) != 0)
                    break;
            } while(header.next_block != -1);

            return entries;
        }
    }
}