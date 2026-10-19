using System;
using System.Collections.Generic;
using System.Linq;

namespace Winland;

/// <summary>
/// Left-to-right order of the tiles in a workspace's strip, as pure list operations. Every operation
/// returns a new list and leaves its input untouched.
///
/// The order is metadata only: which windows belong to a workspace is decided elsewhere (the workspace
/// engine's membership map), and <see cref="Reconcile"/> brings a stored order back in line with it.
/// </summary>
internal static class TileOrder
{
    /// <summary>
    /// Keep the known order of the items that are still <paramref name="members"/>, drop the rest, and
    /// append members the order doesn't know yet.
    /// </summary>
    public static List<T> Reconcile<T>(IReadOnlyList<T> order, IEnumerable<T> members) where T : notnull
    {
        List<T> current = members.ToList();
        var isMember = new HashSet<T>(current);
        var seen = new HashSet<T>();

        List<T> result = order.Where(item => isMember.Contains(item) && seen.Add(item)).ToList();
        result.AddRange(current.Where(seen.Add));
        return result;
    }

    /// <summary>Put <paramref name="item"/> right after <paramref name="anchor"/>, or at the end without one.</summary>
    public static List<T> InsertAfter<T>(IReadOnlyList<T> order, T item, T anchor) where T : notnull
    {
        List<T> result = Remove(order, item);
        int at = result.IndexOf(anchor);
        result.Insert(at < 0 ? result.Count : at + 1, item);
        return result;
    }

    /// <summary>Put <paramref name="item"/> at <paramref name="index"/>, clamped to the strip.</summary>
    public static List<T> InsertAt<T>(IReadOnlyList<T> order, T item, int index) where T : notnull
    {
        List<T> result = Remove(order, item);
        result.Insert(Math.Clamp(index, 0, result.Count), item);
        return result;
    }

    /// <summary>
    /// Move <paramref name="item"/> into <paramref name="target"/>'s place; the tiles in between shift one
    /// step toward where it came from (for neighbours, a swap). Unknown items leave the order unchanged.
    /// </summary>
    public static List<T> MoveTo<T>(IReadOnlyList<T> order, T item, T target) where T : notnull
    {
        List<T> result = order.ToList();
        int from = result.IndexOf(item);
        int to = result.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
        {
            return result;
        }

        result.RemoveAt(from);
        result.Insert(to, item);
        return result;
    }

    public static List<T> Remove<T>(IReadOnlyList<T> order, T item) where T : notnull =>
        order.Where(existing => !EqualityComparer<T>.Default.Equals(existing, item)).ToList();
}
