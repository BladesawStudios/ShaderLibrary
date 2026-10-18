using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ShaderLibrary.Helpers
{
    public class ShaderOptionSearcher
    {
        public static int GetProgramIndex(ShaderModel shader, Dictionary<string, string> options)
        {
            //Generate keys of the shader options
            int[] key_lookup = WriteOptionKeys(shader, options);
            if (ProgramsByKey(shader).TryGetValue(key_lookup, out int found))
                return found;

            Debug.WriteLine($"Failed to find shader. Using backup check.");

            //Temp fall back to previous search method
            //Should not need to run unless the shader options given are incomplete (ie missing render info options)
            return FirstValidProgram(shader, options);
        }

        // The choice each program has for each option, decoded once per shading model: a shading model has thousands of programs and a program that is not an exact match
        // is found by looking at them one after another, each time decoding its key for every option given, by name.
        sealed class Columns(string[] names, int[][] choiceOfProgram)
        {
            public readonly string[] Names = names;
            public readonly int[][] ChoiceOfProgram = choiceOfProgram;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderModel, Columns> ColumnsOf = new();

        static Columns ColumnsFor(ShaderModel shader)
        {
            if (ColumnsOf.TryGetValue(shader, out var ready))
                return ready;
            lock (ColumnsOf)
            {
                if (ColumnsOf.TryGetValue(shader, out var held))
                    return held;

                int keys = shader.StaticKeyLength + shader.DynamicKeyLength;
                var names = new List<string>();
                var columns = new List<int[]>();
                foreach (var option in shader.StaticOptions.Values)
                {
                    var column = new int[shader.Programs.Count];
                    for (int i = 0; i < column.Length; i++)
                        column[i] = option.GetChoiceIndex(shader.KeyTable[keys * i + option.Bit32Index]);
                    names.Add(option.Name);
                    columns.Add(column);
                }
                foreach (var option in shader.DynamicOptions.Values)
                {
                    var column = new int[shader.Programs.Count];
                    int at = shader.StaticKeyLength + option.Bit32Index - option.KeyOffset;
                    for (int i = 0; i < column.Length; i++)
                        column[i] = option.GetChoiceIndex(shader.KeyTable[keys * i + at]);
                    names.Add(option.Name);
                    columns.Add(column);
                }
                var made = new Columns([.. names], [.. columns]);
                ColumnsOf.Add(shader, made);
                return made;
            }
        }

        // The first program whose choice for every option given is the one asked for: what walking the programs with IsValidProgram finds, without decoding a key for each.
        static int FirstValidProgram(ShaderModel shader, Dictionary<string, string> options)
        {
            var columns = ColumnsFor(shader);
            var wanted = new List<(int[] Column, int Choice)>();
            foreach (var (name, value) in options)
            {
                int at = Array.IndexOf(columns.Names, name);
                if (at < 0)
                    continue;

                // A value that is no choice of the option is none of the programs' choices.
                var option = shader.StaticOptions.ContainsKey(name) ? shader.StaticOptions[name] : shader.DynamicOptions[name];
                int choice = option.Choices.GetIndex(value);
                if (choice < 0)
                    return -1;
                wanted.Add((columns.ChoiceOfProgram[at], choice));
            }

            for (int i = 0; i < shader.Programs.Count; i++)
            {
                bool all = true;
                foreach (var (column, choice) in wanted)
                    if (column[i] != choice)
                    {
                        all = false;
                        break;
                    }
                if (all)
                    return i;
            }
            return -1;
        }

        // The first program of each key row, so a lookup is a hash and not a walk over every program: a shader has thousands of them and a model asks for one
        // per material. A row that more than one program has goes to the first, as the walk it replaces did.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderModel, Dictionary<int[], int>> RowIndex = new();

        static Dictionary<int[], int> ProgramsByKey(ShaderModel shader)
        {
            if (RowIndex.TryGetValue(shader, out var ready))
                return ready;
            lock (RowIndex)
            {
                if (RowIndex.TryGetValue(shader, out var held))
                    return held;
                int length = shader.StaticKeyLength + shader.DynamicKeyLength;
                var rows = new Dictionary<int[], int>(shader.Programs.Count, KeyRowComparer.Instance);
                for (int i = 0; i < shader.Programs.Count; i++)
                {
                    int at = length * i;
                    if (at < 0 || at + length > shader.KeyTable.Length)
                        break;
                    rows.TryAdd(shader.KeyTable.AsSpan(at, length).ToArray(), i);
                }
                RowIndex.Add(shader, rows);
                return rows;
            }
        }

        sealed class KeyRowComparer : IEqualityComparer<int[]>
        {
            public static readonly KeyRowComparer Instance = new();

            public bool Equals(int[]? x, int[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

            public int GetHashCode(int[] row)
            {
                var hash = new HashCode();
                hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row.AsSpan()));
                return hash.ToHashCode();
            }
        }

        public static int[] WriteOptionKeys(ShaderModel shader, Dictionary<string, string> options)
        {
            //Setup default keys
            int[] key_lookup = WriteDefaultKey(shader);

            //Setup static and dynamic keys
            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                if (!options.ContainsKey(option.Name))
                    continue;

                //Set the static option choice
                int choiceIndex = option.Choices.GetIndex(options[option.Name]);
                if (choiceIndex == -1)
                    throw new Exception(string.Format("Invalid choice given {1} for option {0}!", option.Name, options[option.Name]));

                option.SetKey(ref key_lookup[option.Bit32Index], choiceIndex);
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                if (!options.ContainsKey(option.Name))
                    continue;

                //Set the dynamic option choice
                int choiceIndex = option.Choices.GetIndex(options[option.Name]);
                if (choiceIndex == -1)
                    throw new Exception(string.Format("Invalid choice given {1} for option {0}!", option.Name, options[option.Name]));

                int ind = option.Bit32Index - option.KeyOffset;
                option.SetKey(ref key_lookup[shader.StaticKeyLength + ind], choiceIndex);
            }
            return key_lookup;
        }

        static int[] WriteDefaultKey(ShaderModel shader)
        {
            int num_keys = shader.StaticKeyLength + shader.DynamicKeyLength;

            int[] keys = new int[num_keys];

            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                //Set the default static option choice
                option.SetKey(ref keys[option.Bit32Index], option.DefaultChoiceIdx);
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];

                //Set the default dynamic option choice
                option.SetKey(ref keys[option.Bit32Index], option.DefaultChoiceIdx);
            }

            return keys;
        }

        public static bool IsValidProgram(ShaderModel shader, int programIndex, Dictionary<string, string> options)
        {
            //The amount of keys used per program
            int numKeysPerProgram = shader.StaticKeyLength + shader.DynamicKeyLength;

            //Static key (total * program index)
            int baseIndex = numKeysPerProgram * programIndex;

            foreach (var option in shader.StaticOptions.Values)
            {
                //The options must be the same between bfres and bfsha
                if (!options.ContainsKey(option.Name))
                    continue;

                //Get key in table
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + option.Bit32Index]);
                if (choiceIndex > option.Choices.Count)
                    throw new Exception($"Invalid choice index in key table! Option {option.Name} choice {options[option.Name]}");

                //If the choice is not in the program, then skip the current program
                var choice = option.Choices.GetKey(choiceIndex);
                if (options[option.Name] != choice)
                    return false;
            }

            foreach (var option in shader.DynamicOptions.Values)
            {
                if (!options.ContainsKey(option.Name))
                    continue;

                int ind = option.Bit32Index - option.KeyOffset;
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + shader.StaticKeyLength + ind]);
                if (choiceIndex > option.Choices.Count)
                    throw new Exception($"Invalid choice index in key table!");

                var choice = option.Choices.GetKey(choiceIndex);
                if (options[option.Name] != choice)
                    return false;
            }
            return true;
        }

        //Checks if the shader option list is missing any shader option choices required for a full key search
        public static void CheckMissingShaderOptions(ShaderModel shader, Dictionary<string, string> options)
        {
            int num_keys_per_program = shader.StaticKeyLength + shader.DynamicKeyLength;
            for (int i = 0; i < shader.Programs.Count; i++)
            {
                if (IsValidProgram(shader, i, options))
                    CheckChoices(shader, i, options);
            }
        }

        static void CheckChoices(ShaderModel shader, int programIndex, Dictionary<string, string> options)
        {
            Debug.WriteLine($"checking program {programIndex}");

            int numKeysPerProgram = shader.StaticKeyLength + shader.DynamicKeyLength;

            var maxBit = shader.StaticOptions.Values.Max(x => x.Bit32Index);
            int baseIndex = numKeysPerProgram * programIndex;
            for (int j = 0; j < shader.StaticOptions.Count; j++)
            {
                var option = shader.StaticOptions[j];
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + option.Bit32Index]);
                if (choiceIndex > option.Choices.Count || choiceIndex == -1)
                    throw new Exception($"Invalid choice index in key table! {option.Name} index {choiceIndex}");

                string choice = option.Choices.GetKey(choiceIndex);

                //A shader option choice not set in the lookup and not a default choice
                //This must be set for a valid lookup
                if (!options.ContainsKey(option.Name) && choice != option.DefaultChoice)
                    Debug.WriteLine($"Unexpected choice value {option.Name} should be {choice}, not default {option.DefaultChoice}");
            }

            for (int j = 0; j < shader.DynamicOptions.Count; j++)
            {
                var option = shader.DynamicOptions[j];
                int ind = option.Bit32Index - option.KeyOffset;
                int choiceIndex = option.GetChoiceIndex(shader.KeyTable[baseIndex + shader.StaticKeyLength + ind]);
                if (choiceIndex > option.Choices.Count || choiceIndex == -1)
                    throw new Exception($"Invalid choice index in key table! {option.Name} index {choiceIndex}");


                string choice = option.Choices.GetKey(choiceIndex);
                if (!options.ContainsKey(option.Name) && choice != option.DefaultChoice)
                    Debug.WriteLine($"Unexpected choice value {option.Name} should be {choice}, not default {option.DefaultChoice}");
            }
        }
    }
}
