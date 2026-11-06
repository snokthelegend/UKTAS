using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// The contents of one <c>List&lt;T&gt;</c> field whose elements are Unity objects
    /// (enemies, rigidbodies, other magnets). Plain field capture skips lists, which is why
    /// the magnets a Magnet holds (sawblades, chainsaws, connected magnets, stuck enemies)
    /// were lost on load.
    /// </summary>
    public sealed class ListRefState
    {
        public FieldInfo Field;
        public UnityEngine.Object[] Items;
    }

    public sealed partial class SaveStateManager
    {
        static readonly Dictionary<Type, FieldInfo[]> ListFieldCache = new Dictionary<Type, FieldInfo[]>();

        static bool IsObjectList(FieldInfo field)
        {
            Type type = field.FieldType;
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>))
                return false;
            return typeof(UnityEngine.Object).IsAssignableFrom(type.GetGenericArguments()[0]);
        }

        static FieldInfo[] GetObjectListFields(Type type)
        {
            FieldInfo[] cached;
            if (ListFieldCache.TryGetValue(type, out cached))
                return cached;

            List<FieldInfo> fields = new List<FieldInfo>(4);
            for (Type current = type;
                 current != null && current != typeof(MonoBehaviour) && current != typeof(object);
                 current = current.BaseType)
            {
                FieldInfo[] declared = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    if (IsObjectList(declared[i]))
                        fields.Add(declared[i]);
                }
            }
            cached = fields.ToArray();
            ListFieldCache[type] = cached;
            return cached;
        }

        /// <summary>Snapshot every Unity-object list on the component, keeping only items alive now.</summary>
        static List<ListRefState> CaptureListRefs(object target)
        {
            if (target == null)
                return null;
            FieldInfo[] fields = GetObjectListFields(target.GetType());
            if (fields.Length == 0)
                return null;

            List<ListRefState> result = new List<ListRefState>(fields.Length);
            for (int f = 0; f < fields.Length; f++)
            {
                IList list;
                try { list = fields[f].GetValue(target) as IList; }
                catch { continue; }
                if (list == null)
                    continue;

                List<UnityEngine.Object> items = new List<UnityEngine.Object>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    UnityEngine.Object item = list[i] as UnityEngine.Object;
                    if (item != null)
                        items.Add(item);
                }
                result.Add(new ListRefState { Field = fields[f], Items = items.ToArray() });
            }
            return result;
        }

        /// <summary>
        /// Put the saved members back. Items destroyed since the save are left out; items that
        /// were not in the list at save time are removed by the Clear, so membership matches
        /// the saved moment exactly.
        /// </summary>
        static void RestoreListRefs(object target, List<ListRefState> refs)
        {
            if (target == null || refs == null)
                return;
            for (int r = 0; r < refs.Count; r++)
            {
                ListRefState state = refs[r];
                try
                {
                    IList list = state.Field.GetValue(target) as IList;
                    if (list == null)
                        continue;
                    list.Clear();
                    for (int i = 0; i < state.Items.Length; i++)
                    {
                        if (GameBridge.IsAlive(state.Items[i]))
                            list.Add(state.Items[i]);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
