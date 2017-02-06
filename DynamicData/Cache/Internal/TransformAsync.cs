﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DynamicData.Kernel;

namespace DynamicData.Cache.Internal
{
    internal class TransformAsync<TDestination, TSource, TKey>
    {
        private readonly IObservable<IChangeSet<TSource, TKey>> _source;
        private readonly Action<Error<TSource, TKey>> _exceptionCallback;
        private readonly Func<TKey, TSource,  Task<TDestination>> _transformFactory;
        private readonly int _maximumConcurrency;

        public TransformAsync(IObservable<IChangeSet<TSource, TKey>> source, Action<Error<TSource, TKey>> exceptionCallback, Func<TKey, TSource, Task<TDestination>> transformFactory, int maximumConcurrency = 1)
        {
            _source = source;
            _exceptionCallback = exceptionCallback;
            _transformFactory = transformFactory;
            _maximumConcurrency = maximumConcurrency;
        }
        
        public IObservable<IChangeSet<TDestination, TKey>> Run()
        {
            return Observable.Create<IChangeSet<TDestination, TKey>>(observer =>
            {
                var cache = new ChangeAwareCache<TransformedItemContainer, TKey>();

                var transformer = _source.Select( changes =>
                {
                    return Observable.FromAsync(() => DoTransform(cache, changes)).Wait();
                });

                var notifier = transformer.SubscribeSafe(observer);
                
                return new CompositeDisposable(notifier);
            });

        }

        private async Task<IChangeSet<TDestination, TKey>> DoTransform(ChangeAwareCache<TransformedItemContainer, TKey>  cache, IChangeSet<TSource, TKey> changes )
        {
            var transformed = await changes.SelectParallel(c => Transform(cache, c), _maximumConcurrency);
            return ProcessUpdates(cache, transformed.ToArray());
        }

        private async Task<TransformResult> Transform(ChangeAwareCache<TransformedItemContainer, TKey> target, Change<TSource, TKey> change)
        {
            try
            {
                if (change.Reason == ChangeReason.Add || change.Reason == ChangeReason.Update)
                {
                    var destination = await _transformFactory(change.Key, change.Current);
                    return new TransformResult(change, new TransformedItemContainer(change.Key, change.Current, destination));
                }

                var existing = target.Lookup(change.Key)
                    .ValueOrThrow(()=> CreateMissingKeyException(change.Reason, change.Key));

                return new TransformResult(change, existing);
            }
            catch (Exception ex)
            {
                //only handle errors if a handler has been specified
                if (_exceptionCallback != null)
                    return new TransformResult(change, ex);

                throw;
            }
        }

        private Exception CreateMissingKeyException(ChangeReason reason, TKey key)
        {
            var message = $"{key} is missing. The change reason is '{reason}'." +
                          $"{Environment.NewLine}Object type {typeof(TSource)}, Key type {typeof(TKey)}, Destination type is {typeof(TDestination)}";
            return new MissingKeyException(message);
        }

        private IChangeSet<TDestination, TKey> ProcessUpdates(ChangeAwareCache<TransformedItemContainer, TKey> cache, TransformResult[] transformedItems)
            {
                //check for errors and callback if a handler has been specified
                var errors = transformedItems.Where(t => !t.Success).ToArray();
                if (errors.Any())
                    errors.ForEach(t => _exceptionCallback(new Error<TSource, TKey>(t.Error, t.Change.Current, t.Change.Key)));

                foreach (var result in transformedItems)
                {
                    if (!result.Success)
                        continue;

                    TKey key = result.Container.Key;
                    switch (result.Change.Reason)
                    {
                        case ChangeReason.Add:
                        case ChangeReason.Update:
                        cache.AddOrUpdate(result.Container, key);
                            break;

                        case ChangeReason.Remove:
                        cache.Remove(key);
                            break;

                        case ChangeReason.Evaluate:
                        cache.Evaluate(key);
                            break;
                    }
                }

                var changes = cache.CaptureChanges();
                var transformed = changes.Select(change => new Change<TDestination, TKey>(change.Reason,
                                                                                          change.Key,
                                                                                          change.Current.Destination,
                                                                                          change.Previous.Convert(x => x.Destination),
                                                                                          change.CurrentIndex,
                                                                                          change.PreviousIndex));

                return new ChangeSet<TDestination, TKey>(transformed);
            }

        private sealed class TransformResult
        {
            public Change<TSource, TKey> Change { get; }
            public Exception Error { get; }
            public bool Success { get; }

            public TransformedItemContainer Container { get; }

            public TransformResult(Change<TSource, TKey> change, TransformedItemContainer container)
            {
                Change = change;
                Container = container;
                Success = true;
            }

            public TransformResult(Change<TSource, TKey> change, Exception error)
            {
                Change = change;
                Error = error;
                Success = false;
            }
        }
        protected sealed class TransformedItemContainer
        {
            public TKey Key { get; }
            public TSource Source { get; }
            public TDestination Destination { get; }

            public TransformedItemContainer(TKey key, TSource source, TDestination destination)
            {
                Key = key;
                Source = source;
                Destination = destination;
            }
        }


    }
}