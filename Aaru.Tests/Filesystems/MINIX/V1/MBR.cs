﻿// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : MINIX.cs
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

using System.Collections.Generic;
using System.IO;
using Aaru.CommonTypes;
using Aaru.CommonTypes.Interfaces;
using Aaru.DiscImages;
using Aaru.Filesystems;
using Aaru.Filters;
using NUnit.Framework;

namespace Aaru.Tests.Filesystems.MINIX.V1
{
    [TestFixture]
    public class MBR
    {
        readonly string[] _testFiles =
        {
            "linux.aif", "minix_3.1.2a.aif", "linux_4.19_minix1_flashdrive.aif"
        };

        readonly ulong[] _sectors =
        {
            262144, 102400, 131072
        };

        readonly uint[] _sectorSize =
        {
            512, 512, 512
        };

        readonly long[] _clusters =
        {
            65535, 50399, 64512
        };

        readonly int[] _clusterSize =
        {
            1024, 1024, 1024
        };

        readonly string[] _types =
        {
            "Minix v1", "Minix 3 v1", "Minix v1"
        };

        [Test]
        public void Test()
        {
            for(int i = 0; i < _testFiles.Length; i++)
            {
                string location = Path.Combine(Consts.TEST_FILES_ROOT, "Filesystems", "MINIX v1 filesystem (MBR)",
                                               _testFiles[i]);

                IFilter filter = new ZZZNoFilter();
                filter.Open(location);
                IMediaImage image = new AaruFormat();
                Assert.AreEqual(true, image.Open(filter), _testFiles[i]);
                Assert.AreEqual(_sectors[i], image.Info.Sectors, _testFiles[i]);
                Assert.AreEqual(_sectorSize[i], image.Info.SectorSize, _testFiles[i]);
                List<Partition> partitions = Core.Partitions.GetAll(image);
                IFilesystem     fs         = new MinixFS();
                int             part       = -1;

                for(int j = 0; j < partitions.Count; j++)
                    if(partitions[j].Type == "0x80" ||
                       partitions[j].Type == "0x81" ||
                       partitions[j].Type == "MINIX")
                    {
                        part = j;

                        break;
                    }

                Assert.AreNotEqual(-1, part, $"Partition not found on {_testFiles[i]}");
                Assert.AreEqual(true, fs.Identify(image, partitions[part]), _testFiles[i]);
                fs.GetInformation(image, partitions[part], out _, null);
                Assert.AreEqual(_clusters[i], fs.XmlFsType.Clusters, _testFiles[i]);
                Assert.AreEqual(_clusterSize[i], fs.XmlFsType.ClusterSize, _testFiles[i]);
                Assert.AreEqual(_types[i], fs.XmlFsType.Type, _testFiles[i]);
            }
        }
    }
}