// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Joliet.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : ISO9660 filesystem plugin.
//
// --[ Description ] ----------------------------------------------------------
//
//     Joliet extensions structures.
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
using System.Text;

namespace DiscImageChef.Filesystems.ISO9660
{
    public partial class ISO9660
    {
        static DecodedVolumeDescriptor DecodeJolietDescriptor(PrimaryVolumeDescriptor jolietvd)
        {
            DecodedVolumeDescriptor decodedVD = new DecodedVolumeDescriptor
            {
                SystemIdentifier = Encoding.BigEndianUnicode.GetString(jolietvd.system_id).TrimEnd().Trim('\u0000'),
                VolumeIdentifier = Encoding.BigEndianUnicode.GetString(jolietvd.volume_id).TrimEnd().Trim('\u0000'),
                VolumeSetIdentifier =
                    Encoding.BigEndianUnicode.GetString(jolietvd.volume_set_id).TrimEnd().Trim('\u0000'),
                PublisherIdentifier =
                    Encoding.BigEndianUnicode.GetString(jolietvd.publisher_id).TrimEnd().Trim('\u0000'),
                DataPreparerIdentifier =
                    Encoding.BigEndianUnicode.GetString(jolietvd.preparer_id).TrimEnd().Trim('\u0000'),
                ApplicationIdentifier =
                    Encoding.BigEndianUnicode.GetString(jolietvd.application_id).TrimEnd().Trim('\u0000')
            };

            if(jolietvd.creation_date[0] < 0x31 || jolietvd.creation_date[0] > 0x39)
                decodedVD.CreationTime = DateTime.MinValue;
            else decodedVD.CreationTime = DateHandlers.Iso9660ToDateTime(jolietvd.creation_date);

            if(jolietvd.modification_date[0] < 0x31 || jolietvd.modification_date[0] > 0x39)
                decodedVD.HasModificationTime = false;
            else
            {
                decodedVD.HasModificationTime = true;
                decodedVD.ModificationTime = DateHandlers.Iso9660ToDateTime(jolietvd.modification_date);
            }

            if(jolietvd.expiration_date[0] < 0x31 || jolietvd.expiration_date[0] > 0x39)
                decodedVD.HasExpirationTime = false;
            else
            {
                decodedVD.HasExpirationTime = true;
                decodedVD.ExpirationTime = DateHandlers.Iso9660ToDateTime(jolietvd.expiration_date);
            }

            if(jolietvd.effective_date[0] < 0x31 || jolietvd.effective_date[0] > 0x39)
                decodedVD.HasEffectiveTime = false;
            else
            {
                decodedVD.HasEffectiveTime = true;
                decodedVD.EffectiveTime = DateHandlers.Iso9660ToDateTime(jolietvd.effective_date);
            }

            decodedVD.Blocks = jolietvd.volume_space_size;
            decodedVD.BlockSize = jolietvd.logical_block_size;

            return decodedVD;
        }
    }
}