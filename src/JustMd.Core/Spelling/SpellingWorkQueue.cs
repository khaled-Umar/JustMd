using System.Collections.Generic;
using System.Linq;

namespace JustMd.Core.Spelling
{
    /// <summary>
    /// The spelling worker's queues: urgent work runs before a document scan, and forgetting a tab
    /// drops what it has queued and marks what is already running as stale. Not thread-safe; the owner locks.
    /// </summary>
    public sealed class SpellingWorkQueue<T>
    {
        /// <summary>For work that must still happen if its tab goes away. Never forgotten.</summary>
        public const int NoTab = 0;

        private readonly Queue<Item> _urgent = new();
        private readonly Queue<Item> _low = new();
        private readonly Dictionary<int, int> _generations = new();

        public void Enqueue(int tabId, bool urgent, T work)
        {
            _generations.TryGetValue(tabId, out int generation);
            (urgent ? _urgent : _low).Enqueue(new Item(tabId, generation, work));
        }

        public bool TryDequeue(out Item item) => _urgent.TryDequeue(out item) || _low.TryDequeue(out item);

        public void Forget(int tabId)
        {
            _generations.TryGetValue(tabId, out int generation);
            _generations[tabId] = generation + 1;
            Remove(_urgent, tabId);
            Remove(_low, tabId);
        }

        /// <summary>False once the item's tab has been forgotten, so its result must not be used.</summary>
        public bool IsCurrent(Item item)
        {
            _generations.TryGetValue(item.TabId, out int generation);
            return item.Generation == generation;
        }

        public void Clear()
        {
            _urgent.Clear();
            _low.Clear();
        }

        private static void Remove(Queue<Item> queue, int tabId)
        {
            List<Item> kept = queue.Where(item => item.TabId != tabId).ToList();
            queue.Clear();
            foreach (Item item in kept)
            {
                queue.Enqueue(item);
            }
        }

        public readonly record struct Item(int TabId, int Generation, T Work);
    }
}
