// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
using UdonSharp;


namespace Cesario
{
    /// <summary>
    /// Owns the deferred-dispatch queue for <see cref="Promise"/>. Callback invocation is never
    /// performed synchronously by `Promise.Resolve`/`Reject` - it is handed off to
    /// this scheduler and flushed a frame later, so that subscribers are never
    /// called within the same turn that settled the promise.
    ///
    /// Dispatch goes through SetProgramVariable + SendCustomEvent rather than typed
    /// calls, since U# does not support custom interfaces. Every Then/Catch target
    /// needs a public `Promise IncomingPromise` field; every Chain target additionally
    /// needs a public `object ChainResult` field.
    /// </summary>
    public class PromiseScheduler : UdonSharpBehaviour
    {
        private UdonSharpBehaviour[] _targets = new UdonSharpBehaviour[8];
        private string[] _events = new string[8];
        private Promise[] _sources = new Promise[8];
        private int _count = 0;

        private UdonSharpBehaviour[] _chainQueueTargets = new UdonSharpBehaviour[8];
        private string[] _chainQueueFulfillEvents = new string[8];
        private string[] _chainQueueRejectEvents = new string[8];
        private Promise[] _chainQueueSettled = new Promise[8];
        private Promise[] _chainQueueNext = new Promise[8];
        private int _chainQueueCount = 0;

        private bool _flushScheduled = false;

        public void Enqueue(UdonSharpBehaviour target, string eventName, Promise source)
        {
            if (_count >= _targets.Length) Grow();

            _targets[_count] = target;
            _events[_count] = eventName;
            _sources[_count] = source;
            _count++;

            ScheduleFlush();
        }

        public void EnqueueChain(UdonSharpBehaviour target, string onFulfilledEvent, string onRejectedEvent, Promise settled, Promise next)
        {
            if (_chainQueueCount >= _chainQueueTargets.Length) GrowChainQueue();

            _chainQueueTargets[_chainQueueCount] = target;
            _chainQueueFulfillEvents[_chainQueueCount] = onFulfilledEvent;
            _chainQueueRejectEvents[_chainQueueCount] = onRejectedEvent;
            _chainQueueSettled[_chainQueueCount] = settled;
            _chainQueueNext[_chainQueueCount] = next;
            _chainQueueCount++;

            ScheduleFlush();
        }

        private void ScheduleFlush()
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
            SendCustomEventDelayedFrames(nameof(FlushQueue), 0);
        }

        public void FlushQueue()
        {
            int chainCountThisFlush = _chainQueueCount;
            _chainQueueCount = 0;

            for (int i = 0; i < chainCountThisFlush; i++)
            {
                var target = _chainQueueTargets[i];
                var next = _chainQueueNext[i];
                if (target == null) continue;

                var settled = _chainQueueSettled[i];
                string eventToFire = settled.State == PromiseState.Fulfilled
                    ? _chainQueueFulfillEvents[i]
                    : _chainQueueRejectEvents[i];

                target.SetProgramVariable("IncomingPromise", settled);
                target.SetProgramVariable("ChainResult", null);
                target.SetProgramVariable("ChainResultAdoptee", null);
                target.SendCustomEvent(eventToFire);

                Promise adoptee = (Promise)target.GetProgramVariable("ChainResultAdoptee");
                if (adoptee != null)
                {
                    next.Adopt(adoptee);
                }
                else
                {
                    object result = target.GetProgramVariable("ChainResult");
                    next.Resolve(result);
                }
            }
        }

        private void Grow()
        {
            var newTargets = new UdonSharpBehaviour[_targets.Length * 2];
            var newEvents = new string[_events.Length * 2];
            var newSources = new Promise[_sources.Length * 2];
            _targets.CopyTo(newTargets, 0);
            _events.CopyTo(newEvents, 0);
            _sources.CopyTo(newSources, 0);
            _targets = newTargets;
            _events = newEvents;
            _sources = newSources;
        }

        private void GrowChainQueue()
        {
            var newTargets = new UdonSharpBehaviour[_chainQueueTargets.Length * 2];
            var newFulfill = new string[_chainQueueFulfillEvents.Length * 2];
            var newReject = new string[_chainQueueRejectEvents.Length * 2];
            var newSettled = new Promise[_chainQueueSettled.Length * 2];
            var newNext = new Promise[_chainQueueNext.Length * 2];
            _chainQueueTargets.CopyTo(newTargets, 0);
            _chainQueueFulfillEvents.CopyTo(newFulfill, 0);
            _chainQueueRejectEvents.CopyTo(newReject, 0);
            _chainQueueSettled.CopyTo(newSettled, 0);
            _chainQueueNext.CopyTo(newNext, 0);
            _chainQueueTargets = newTargets;
            _chainQueueFulfillEvents = newFulfill;
            _chainQueueRejectEvents = newReject;
            _chainQueueSettled = newSettled;
            _chainQueueNext = newNext;
        }
    }
}