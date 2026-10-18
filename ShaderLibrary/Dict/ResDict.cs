using ShaderLibrary.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace ShaderLibrary
{
    public class ResDict<T> :  Dictionary<string, T>, IResData where T : IResData, new()
    {
        internal List<Node> _nodes = new List<Node>();

        internal List<Node> GetNodes() => _nodes;

        public ResDict() { }

        public T this[int index]
        {
            get => this[GetKey(index)];
            set => this[GetKey(index)] = value;
        }

        // The keys in the order they are enumerated, with each one's place, which is what an index means here. Dictionary has no way to ask for the nth key, and
        // walking to it made every lookup by position a scan of the whole dictionary. Made again whenever the count has changed since.
        sealed record Order(int Count, string[] Keys, Dictionary<string, int> Index);

        Order _order;

        Order OrderNow()
        {
            var held = _order;
            if (held is not null && held.Count == Count)
                return held;
            var keys = new string[Count];
            var index = new Dictionary<string, int>(Count, StringComparer.Ordinal);
            int i = 0;
            foreach (var key in Keys)
            {
                keys[i] = key;
                index.TryAdd(key, i);
                i++;
            }
            return _order = new Order(Count, keys, index);
        }

        public string GetKey(int index)
        {
            var order = OrderNow();
            return index >= 0 && index < order.Keys.Length ? order.Keys[index] : null;
        }

        public int GetIndex(string key) => key is not null && OrderNow().Index.TryGetValue(key, out int i) ? i : -1;

        public void Read(BinaryDataReader reader)
        {
            reader.ReadUInt32(); //magic
            int numNodes = reader.ReadInt32();

            _nodes.Clear();

            int i = 0;
            for (; numNodes >= 0; numNodes--)
            {
                _nodes.Add(new Node()
                {
                    Reference = reader.ReadUInt32(),
                    IdxLeft = reader.ReadUInt16(),
                    IdxRight = reader.ReadUInt16(),
                    Key = reader.LoadString(reader.ReadUInt64()),
                });
                i++;
            }

            for (int j = 1; j < _nodes.Count; j++)
                this.Add(_nodes[j].Key, new T());
        }

        public void GenerateTree()
        {
            // Update the Patricia trie values in the nodes.
            var newNodes = ResDictUpdate.UpdateNodes(Keys.ToList());

            if (_nodes.Count != newNodes.Length)
                _nodes = newNodes.Select(_ => new Node()).ToList();

            for (int i = 0; i < _nodes.Count; i++)
            {
                _nodes[i].Reference = newNodes[i].Reference;
                _nodes[i].IdxLeft = newNodes[i].IdxLeft;
                _nodes[i].IdxRight = newNodes[i].IdxRight;
                _nodes[i].Key = newNodes[i].Key;
            }
        }

        public void GenerateTreeWiiU()
        {
            // Update the Patricia trie values in the nodes.
            var newNodes = ResDictUpdateWiiU.UpdateNodes(Keys.ToList());

            if (_nodes.Count != newNodes.Length)
                _nodes = newNodes.Select(_ => new Node()).ToList();

            for (int i = 0; i < _nodes.Count; i++)
            {
                _nodes[i].Reference = newNodes[i].Reference;
                _nodes[i].IdxLeft = newNodes[i].IdxLeft;
                _nodes[i].IdxRight = newNodes[i].IdxRight;
                _nodes[i].Key = newNodes[i].Key;
            }
        }

        internal class Node
        {
            internal uint Reference;
            internal ushort IdxLeft;
            internal ushort IdxRight;
            internal string Key;
            internal IResData Value;
        }
    }
}
