// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : ExtentsULong.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Extent helpers.
//
// --[ Description ] ----------------------------------------------------------
//
//     Provides extents for ulong types.
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
//     License aulong with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2018 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;

namespace Extents
{
    /// <summary>
    ///     Implements extents for <see cref="ulong" />
    /// </summary>
    public class ExtentsULong
    {
        List<Tuple<ulong, ulong>> backend;

        /// <summary>
        ///     Initialize an empty list of extents
        /// </summary>
        public ExtentsULong()
        {
            backend = new List<Tuple<ulong, ulong>>();
        }

        /// <summary>
        ///     Initializes extents with an specific list
        /// </summary>
        /// <param name="list">List of extents as tuples "start, end"</param>
        public ExtentsULong(IEnumerable<Tuple<ulong, ulong>> list)
        {
            backend = list.OrderBy(t => t.Item1).ToList();
        }

        /// <summary>
        ///     Gets a count of how many extents are stored
        /// </summary>
        public int Count => backend.Count;

        /// <summary>
        ///     Adds the specified number to the corresponding extent, or creates a new one
        /// </summary>
        /// <param name="item"></param>
        public void Add(ulong item)
        {
            Tuple<ulong, ulong> removeOne = null;
            Tuple<ulong, ulong> removeTwo = null;
            Tuple<ulong, ulong> itemToAdd = null;

            for(int i = 0; i < backend.Count; i++)
            {
                // Already contained in an extent
                if(item >= backend[i].Item1 && item <= backend[i].Item2) return;

                // Expands existing extent start
                if(item == backend[i].Item1 - 1)
                {
                    removeOne = backend[i];

                    if(i > 0 && item == backend[i - 1].Item2 + 1)
                    {
                        removeTwo = backend[i - 1];
                        itemToAdd = new Tuple<ulong, ulong>(backend[i - 1].Item1, backend[i].Item2);
                    }
                    else itemToAdd = new Tuple<ulong, ulong>(item, backend[i].Item2);

                    break;
                }

                // Expands existing extent end
                if(item != backend[i].Item2 + 1) continue;

                removeOne = backend[i];

                if(i < backend.Count - 1 && item == backend[i + 1].Item1 - 1)
                {
                    removeTwo = backend[i + 1];
                    itemToAdd = new Tuple<ulong, ulong>(backend[i].Item1, backend[i + 1].Item2);
                }
                else itemToAdd = new Tuple<ulong, ulong>(backend[i].Item1, item);

                break;
            }

            if(itemToAdd != null)
            {
                backend.Remove(removeOne);
                backend.Remove(removeTwo);
                backend.Add(itemToAdd);
            }
            else backend.Add(new Tuple<ulong, ulong>(item, item));

            // Sort
            backend = backend.OrderBy(t => t.Item1).ToList();
        }

        /// <summary>
        ///     Adds a new extent
        /// </summary>
        /// <param name="start">First element of the extent</param>
        /// <param name="end">
        ///     Last element of the extent or if <see cref="run" /> is <c>true</c> how many elements the extent runs
        ///     for
        /// </param>
        /// <param name="run">If set to <c>true</c>, <see cref="end" /> indicates how many elements the extent runs for</param>
        public void Add(ulong start, ulong end, bool run = false)
        {
            ulong realEnd;
            if(run) realEnd = start + end - 1;
            else realEnd = end;

            // TODO: Optimize this
            for(ulong t = start; t <= realEnd; t++) Add(t);
        }

        /// <summary>
        ///     Checks if the specified item is contained by an extent on this instance
        /// </summary>
        /// <param name="item">Item to seach for</param>
        /// <returns><c>true</c> if any of the extents on this instance contains the item</returns>
        public bool Contains(ulong item)
        {
            return backend.Any(extent => item >= extent.Item1 && item <= extent.Item2);
        }

        /// <summary>
        ///     Removes all extents from this instance
        /// </summary>
        public void Clear()
        {
            backend.Clear();
        }

        /// <summary>
        ///     Removes an item from the extents in this instance
        /// </summary>
        /// <param name="item">Item to remove</param>
        /// <returns><c>true</c> if the item was contained in a known extent and removed, false otherwise</returns>
        public bool Remove(ulong item)
        {
            Tuple<ulong, ulong> toRemove = null;
            Tuple<ulong, ulong> toAddOne = null;
            Tuple<ulong, ulong> toAddTwo = null;

            foreach(Tuple<ulong, ulong> extent in backend)
            {
                // Extent is contained and not a border
                if(item > extent.Item1 && item < extent.Item2)
                {
                    toRemove = extent;
                    toAddOne = new Tuple<ulong, ulong>(extent.Item1, item - 1);
                    toAddTwo = new Tuple<ulong, ulong>(item + 1, extent.Item2);
                    break;
                }

                // Extent is left border, but not only element
                if(item == extent.Item1 && item != extent.Item2)
                {
                    toRemove = extent;
                    toAddOne = new Tuple<ulong, ulong>(item + 1, extent.Item2);
                    break;
                }

                // Extent is right border, but not only element
                if(item != extent.Item1 && item == extent.Item2)
                {
                    toRemove = extent;
                    toAddOne = new Tuple<ulong, ulong>(extent.Item1, item - 1);
                    break;
                }

                // Extent is only element
                if(item != extent.Item1 || item != extent.Item2) continue;

                toRemove = extent;
                break;
            }

            // Item not found
            if(toRemove == null) return false;

            backend.Remove(toRemove);
            if(toAddOne != null) backend.Add(toAddOne);
            if(toAddTwo != null) backend.Add(toAddTwo);

            // Sort
            backend = backend.OrderBy(t => t.Item1).ToList();

            return true;
        }

        /// <summary>
        ///     Converts the list of extents to an array of <see cref="Tuple" /> where T1 is first element of the extent and T2 is
        ///     last element
        /// </summary>
        /// <returns>Array of <see cref="Tuple" /></returns>
        public Tuple<ulong, ulong>[] ToArray()
        {
            return backend.ToArray();
        }

        /// <summary>
        ///     Gets the first element of the extent that contains the specified item
        /// </summary>
        /// <param name="item">Item</param>
        /// <param name="start">First element of extent</param>
        /// <returns><c>true</c> if item was found in an extent, false otherwise</returns>
        public bool GetStart(ulong item, out ulong start)
        {
            start = 0;
            foreach(Tuple<ulong, ulong> extent in backend.Where(extent => item >= extent.Item1 && item <= extent.Item2))
            {
                start = extent.Item1;
                return true;
            }

            return false;
        }
    }
}