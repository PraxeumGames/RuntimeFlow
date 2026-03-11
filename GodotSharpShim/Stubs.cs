using System;
using System.Collections;
using System.Collections.Generic;

namespace Godot
{
    public class GodotObject : IDisposable
    {
        public void Dispose() { }
    }

    public class RefCounted : GodotObject { }
    public class Resource : RefCounted { }
    public class Node : GodotObject { }
    public class PackedScene : Resource { }

    public static class GD
    {
        public static void Print(string what) { }
        public static void Print(params object[] what) { }
    }

    namespace Collections
    {
        public class Array<T> : IEnumerable<T>
        {
            private readonly List<T> _items = new();
            public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public class Dictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
        {
            private readonly System.Collections.Generic.Dictionary<TKey, TValue> _items = new();
            public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
