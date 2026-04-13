using System;
using System.Collections.Generic;

namespace UniRx
{
    public interface IReadOnlyReactiveProperty<out T> : IObservable<T>
    {
        T Value { get; }
    }

    public sealed class ReactiveProperty<T> : IReadOnlyReactiveProperty<T>, IDisposable
    {
        private readonly List<IObserver<T>> _observers = new();
        private T _value;
        private bool _isDisposed;

        public ReactiveProperty()
            : this(default!)
        {
        }

        public ReactiveProperty(T initialValue)
        {
            _value = initialValue;
        }

        public T Value
        {
            get => _value;
            set
            {
                ThrowIfDisposed();
                _value = value;
                Notify(value);
            }
        }

        public IDisposable Subscribe(IObserver<T> observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            ThrowIfDisposed();

            _observers.Add(observer);
            observer.OnNext(_value);
            return new Subscription(this, observer);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _observers.Clear();
        }

        private void Notify(T value)
        {
            for (var i = 0; i < _observers.Count; i++)
            {
                _observers[i].OnNext(value);
            }
        }

        private void Unsubscribe(IObserver<T> observer)
        {
            _observers.Remove(observer);
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ReactiveProperty<T>));
            }
        }

        private sealed class Subscription : IDisposable
        {
            private ReactiveProperty<T>? _owner;
            private IObserver<T>? _observer;

            public Subscription(ReactiveProperty<T> owner, IObserver<T> observer)
            {
                _owner = owner;
                _observer = observer;
            }

            public void Dispose()
            {
                if (_owner == null || _observer == null)
                {
                    return;
                }

                _owner.Unsubscribe(_observer);
                _owner = null;
                _observer = null;
            }
        }
    }
}
