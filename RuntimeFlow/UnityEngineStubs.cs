namespace UnityEngine
{
    public class Object
    {
    }

    public class Component : Object
    {
        public T? GetComponentInChildren<T>(bool includeInactive = false)
            where T : class
        {
            return default;
        }
    }
}
