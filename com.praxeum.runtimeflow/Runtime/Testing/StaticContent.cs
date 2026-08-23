using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Testing
{
    /// <summary>A canned content source serving a fixed snapshot — the fastest way to fake any content in tests.</summary>
    public sealed class StaticContent<TData> : RuntimeFlow.Content.ContentSource<TData>
        where TData : class
    {
        private readonly string _sourceName;
        private readonly TData _data;

        public StaticContent(TData data, string sourceName = "static")
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _sourceName = sourceName;
        }

        public override string SourceName => _sourceName;

        protected override Task<TData> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_data);
    }
}
