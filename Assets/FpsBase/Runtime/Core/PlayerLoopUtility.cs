using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;

namespace FpsBase
{
    public static class PlayerLoopUtility
    {
        private enum Placement
        {
            First,
            Last,
            Before,
            After,
        }

        public static void AddFirst(Type phase, Type system, PlayerLoopSystem.UpdateFunction update)
        {
            Add(phase, Placement.First, system, update);
        }

        public static void AddLast(Type phase, Type system, PlayerLoopSystem.UpdateFunction update)
        {
            Add(phase, Placement.Last, system, update);
        }

        public static void AddBefore(Type anchor, Type system, PlayerLoopSystem.UpdateFunction update)
        {
            Add(anchor, Placement.Before, system, update);
        }

        public static void AddAfter(Type anchor, Type system, PlayerLoopSystem.UpdateFunction update)
        {
            Add(anchor, Placement.After, system, update);
        }

        public static void Remove(Type system)
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (RemoveFrom(ref root, system))
            {
                PlayerLoop.SetPlayerLoop(root);
            }
        }

        // Drops any earlier copy first, so installs stay idempotent with domain reload disabled.
        private static void Add(Type target, Placement placement, Type system, PlayerLoopSystem.UpdateFunction update)
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            RemoveFrom(ref root, system);
            var entry = new PlayerLoopSystem { type = system, updateDelegate = update };
            if (!InsertInto(ref root, target, placement, entry))
            {
                Debug.LogWarning($"Player loop has no {target.Name}, so {system.Name} never runs.");
                return;
            }

            PlayerLoop.SetPlayerLoop(root);
        }

        private static bool InsertInto(ref PlayerLoopSystem parent, Type target, Placement placement, PlayerLoopSystem entry)
        {
            PlayerLoopSystem[] children = parent.subSystemList;
            if (children == null)
            {
                return false;
            }

            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].type == target)
                {
                    switch (placement)
                    {
                        case Placement.Before:
                            parent.subSystemList = InsertAt(children, i, entry);
                            break;
                        case Placement.After:
                            parent.subSystemList = InsertAt(children, i + 1, entry);
                            break;
                        case Placement.First:
                            children[i].subSystemList = InsertAt(children[i].subSystemList, 0, entry);
                            break;
                        case Placement.Last:
                            PlayerLoopSystem[] phase = children[i].subSystemList;
                            children[i].subSystemList = InsertAt(phase, phase?.Length ?? 0, entry);
                            break;
                    }
                    return true;
                }

                if (InsertInto(ref children[i], target, placement, entry))
                {
                    return true;
                }
            }
            return false;
        }

        private static PlayerLoopSystem[] InsertAt(PlayerLoopSystem[] systems, int index, PlayerLoopSystem entry)
        {
            var list = new List<PlayerLoopSystem>(systems ?? Array.Empty<PlayerLoopSystem>());
            list.Insert(index, entry);
            return list.ToArray();
        }

        private static bool RemoveFrom(ref PlayerLoopSystem parent, Type system)
        {
            if (parent.subSystemList == null)
            {
                return false;
            }

            var children = new List<PlayerLoopSystem>(parent.subSystemList);
            bool removed = children.RemoveAll(child => child.type == system) > 0;
            for (int i = 0; i < children.Count; i++)
            {
                PlayerLoopSystem child = children[i];
                if (RemoveFrom(ref child, system))
                {
                    children[i] = child;
                    removed = true;
                }
            }

            if (removed)
            {
                parent.subSystemList = children.ToArray();
            }
            return removed;
        }
    }
}
