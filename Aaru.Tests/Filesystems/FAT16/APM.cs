﻿// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : FAT16.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Aaru unit testing.
//
// --[ License ] --------------------------------------------------------------
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as
//     published by the Free Software Foundation, either version 3 of the
//     License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2021 Natalia Portillo
// ****************************************************************************/

using System.IO;
using Aaru.CommonTypes;
using Aaru.CommonTypes.Interfaces;
using Aaru.Filesystems;
using NUnit.Framework;

namespace Aaru.Tests.Filesystems.FAT16
{
    [TestFixture]
    public class APM : FilesystemTest
    {
        public APM() : base("FAT16") {}

        public override string      DataFolder => Path.Combine(Consts.TEST_FILES_ROOT, "Filesystems", "FAT16 (APM)");
        public override IFilesystem Plugin     => new FAT();
        public override bool        Partitions => true;

        public override FileSystemTest[] Tests => new[]
        {
            new FileSystemTest
            {
                TestFile     = "macosx_10.3.aif",
                MediaType    = MediaType.GENERIC_HDD,
                Sectors      = 262144,
                SectorSize   = 512,
                Bootable     = true,
                Clusters     = 65076,
                ClusterSize  = 2048,
                SystemId     = "BSD  4.4",
                VolumeName   = "VOLUMELABEL",
                VolumeSerial = "C45B0908"
            },
            new FileSystemTest
            {
                TestFile     = "macosx_10.4.aif",
                MediaType    = MediaType.GENERIC_HDD,
                Sectors      = 262144,
                SectorSize   = 512,
                Bootable     = true,
                Clusters     = 65516,
                ClusterSize  = 2048,
                SystemId     = "BSD  4.4",
                VolumeName   = "VOLUMELABEL",
                VolumeSerial = "24660C0F"
            },
            new FileSystemTest
            {
                TestFile     = "macosx_10.11.aif",
                MediaType    = MediaType.GENERIC_HDD,
                Sectors      = 1024000,
                SectorSize   = 512,
                Bootable     = true,
                Clusters     = 63995,
                ClusterSize  = 8192,
                SystemId     = "BSD  4.4",
                VolumeName   = "VOLUMELABEL",
                VolumeSerial = "063D1F09"
            }
        };
    }
}