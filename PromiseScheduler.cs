// Copyright 2026 (c) Ayase Minori, Umamusume Racing Society Contributors
// Licensed under MIT License
using UdonSharp;

namespace Cesario
{
    /// <summary>
    /// Owns the deferred-dispatch queue for <see cref="Promise"/>. Callback invocation
    /// is never performed synchronously by <c>Promise.Resolve</c>/<c>Reject</c> — it is
    /// handed off to this scheduler and flushed a frame later, so that subscribers are
    /// never called within the same turn that settled the promise.
    ///
    /// Dispatch goes through <c>SetProgramVariable</c> + <c>SendCustomEvent</c> rather
    /// than typed calls, since U# does not support custom interfaces. Every Then/Catch
    /// target needs a public <c>Promise IncomingPromise</c> field; every Chain target
    /// additionally needs a public <c>object ChainResult</c> field (and optionally
    /// <c>Promise ChainResultAdoptee</c>).
    ///
    /// Chain handler dispatch is wrapped in try/catch (Promises/A+ 2.2.7.2): if a
    /// chain handler throws instead of returning normally, the promise it was meant
    /// to settle rejects with the exception instead of hanging pending forever. This
    /// only applies to Chain dispatch, not plain Then/Catch - those are fire-and-forget
    /// with no downstream promise waiting on their outcome, so there's nothing for a
    /// thrown exception to convert into.
    /// </summary>
    public class PromiseScheduler : UdonSharpBehaviour
    {
        #region Normal Then/Catch Queue
        
        private UdonSharpBehaviour[] _targets = new UdonSharpBehaviour[32];
        private string[] _events = new string[32];
        private Promise[] _sources = new Promise[32];
        private int _count = 0;
        
        #endregion
        
        #region Chain Queue
        
        private UdonSharpBehaviour[] _chainQueueTargets = new UdonSharpBehaviour[16];
        private string[] _chainQueueFulfillEvents = new string[16];
        private string[] _chainQueueRejectEvents = new string[16];
        private Promise[] _chainQueueSettled = new Promise[16];
        private Promise[] _chainQueueNext = new Promise[16];
        private int _chainQueueCount = 0;
        
        #endregion

        private bool _flushScheduled = false;

        /// <summary>
        /// Enqueues a normal Then/Catch callback to be invoked on the next frame.
        /// </summary>
        public void Enqueue(UdonSharpBehaviour target, string eventName, Promise source)
        {
            if (_count >= _targets.Length) Grow();

            _targets[_count] = target;
            _events[_count] = eventName;
            _sources[_count] = source;
            _count++;

            ScheduleFlush();
        }

        /// <summary>
        /// Enqueues a Chain handler pair. After the handler runs, the scheduler reads
        /// <c>ChainResult</c> / <c>ChainResultAdoptee</c> and settles <paramref name="next"/>.
        /// </summary>
        public void EnqueueChain(
            UdonSharpBehaviour target,
            string onFulfilledEvent,
            string onRejectedEvent,
            Promise settled,
            Promise next)
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

        /// <summary>
        /// Flushes both the normal and chain queues. Invoked one frame after the first
        /// enqueue of a batch.
        /// </summary>
        public void FlushQueue()
        {
            _flushScheduled = false;

            // 1. Normal Then / Catch
            var normalCount = _count;
            _count = 0;

            for (var i = 0; i < normalCount; i++)
            {
                var target = _targets[i];
                if (target == null) continue;

                target.SetProgramVariable("IncomingPromise", _sources[i]);
                target.SendCustomEvent(_events[i]);
            }

            // 2. Chain handlers
            var chainCountThisFlush = _chainQueueCount;
            _chainQueueCount = 0;

            for (var i = 0; i < chainCountThisFlush; i++)
            {
                var target = _chainQueueTargets[i];
                var next = _chainQueueNext[i];
                if (target == null) continue;

                var settled = _chainQueueSettled[i];
                var eventToFire = settled.State == PromiseState.Fulfilled
                    ? _chainQueueFulfillEvents[i]
                    : _chainQueueRejectEvents[i];

                target.SetProgramVariable("IncomingPromise", settled);
                target.SetProgramVariable("ChainResult", null);
                target.SetProgramVariable("ChainResultAdoptee", null);

                var handlerThrew = false;

                try
                {
                    target.SendCustomEvent(eventToFire);
                }
                catch (System.Exception e)
                {
                    handlerThrew = true;
                    next.Reject(e.Message);
                }

                if (handlerThrew) continue; // handler didn't finish normally - nothing to read back

                var adoptee = (Promise)target.GetProgramVariable("ChainResultAdoptee");
                if (adoptee != null)
                {
                    next.Adopt(adoptee);
                }
                else
                {
                    var result = target.GetProgramVariable("ChainResult");
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