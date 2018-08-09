/* Copyright (c) 2018 Rick (rick 'at' gibbed 'dot' us)
 * 
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 * 
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 * 
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 * 
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 * 
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Gibbed.IO;
using NDesk.Options;

namespace Gibbed.DeadCells.Unpack
{
    internal class Program
    {
        private static string GetExecutableName()
        {
            return Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        }

        private struct PakStackDirectory
        {
            public readonly PakDirectory Directory;
            public readonly uint Index;
            public readonly uint Count;

            public PakStackDirectory(PakDirectory directory, uint index, uint count)
            {
                this.Directory = directory;
                this.Index = index;
                this.Count = count;
            }
        }

        private class PakDirectory
        {
            public PakDirectory Parent;
            public string Name;
            public readonly List<PakDirectory> Subdirectories;
            public readonly List<PakFile> Files;

            public PakDirectory()
            {
                this.Subdirectories = new List<PakDirectory>();
                this.Files = new List<PakFile>();
            }
        }

        private class PakFile
        {
            public PakDirectory Parent;
            public string Name;
            public uint Offset;
            public uint Size;
            public uint Hash;
        }

        public static void Main(string[] args)
        {
            bool showHelp = false;
            bool overwriteFiles = false;
            bool verbose = false;

            var options = new OptionSet()
            {
                { "o|overwrite", "overwrite existing files", v => overwriteFiles = v != null },
                { "v|verbose", "be verbose", v => verbose = v != null },
                { "h|help", "show this message and exit", v => showHelp = v != null },
            };

            List<string> extras;

            try
            {
                extras = options.Parse(args);
            }
            catch (OptionException e)
            {
                Console.Write("{0}: ", GetExecutableName());
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `{0} --help' for more information.", GetExecutableName());
                return;
            }

            if (extras.Count < 1 || extras.Count > 2 || showHelp == true)
            {
                Console.WriteLine("Usage: {0} [OPTIONS]+ input_arc [output_dir]", GetExecutableName());
                Console.WriteLine();
                Console.WriteLine("Options:");
                options.WriteOptionDescriptions(Console.Out);
                return;
            }

            var inputPath = Path.GetFullPath(extras[0]);
            var outputPath = extras.Count > 1 ? extras[1] : Path.ChangeExtension(inputPath, null) + "_unpack";

            using (var input = File.OpenRead(inputPath))
            {
                const uint signature = 0x004B4150;
                var magic = input.ReadValueU32(Endian.Little);
                if (magic != signature && magic.Swap() != signature)
                {
                    throw new FormatException();
                }
                var endian = magic == signature ? Endian.Little : Endian.Big;

                var dataOffset = input.ReadValueU32(endian);
                var dataSize = input.ReadValueU32(endian);

                if (dataOffset + dataSize != input.Length)
                {
                    Console.WriteLine("[warning] Pak file size inconsistent!");
                }

                var rootNameLength = input.ReadValueU8();
                if (rootNameLength != 0)
                {
                    throw new FormatException();
                }

                var rootIsDirectory = input.ReadValueB8();
                if (rootIsDirectory == false)
                {
                    throw new FormatException();
                }
                var rootFileCount = input.ReadValueU32(endian);

                var root = new PakDirectory();
                var files = new List<PakFile>();

                var stack = new Stack<PakStackDirectory>();
                stack.Push(new PakStackDirectory(root, 0, rootFileCount));

                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    var dir = item.Directory;
                    var i = item.Index;
                    var count = item.Count;

                    for (; i < count; i++)
                    {
                        var nameLength = input.ReadValueU8();
                        var name = input.ReadString(nameLength, true, Encoding.UTF8);
                        var isDirectory = input.ReadValueB8();
                        if (isDirectory == false)
                        {
                            var fileOffset = input.ReadValueU32(endian);
                            var fileSize = input.ReadValueU32(endian);
                            var fileHash = input.ReadValueU32(endian);
                            var file = new PakFile()
                            {
                                Parent = dir,
                                Name = name,
                                Offset = fileOffset,
                                Size = fileSize,
                                Hash = fileHash,
                            };
                            dir.Files.Add(file);
                            files.Add(file);
                        }
                        else
                        {
                            var subdir = new PakDirectory()
                            {
                                Parent = dir,
                                Name = name,
                            };
                            dir.Subdirectories.Add(subdir);
                            var subdirFileCount = input.ReadValueU32(endian);
                            stack.Push(new PakStackDirectory(dir, i + 1, count));
                            stack.Push(new PakStackDirectory(subdir, 0, subdirFileCount));
                            break;
                        }
                    }
                }

                var dataMagic = input.ReadValueU32(endian);
                if (dataMagic != 0x41544144) // 'DATA'
                {
                    Console.WriteLine("[warning] Pak header did not end with 'DATA'! Files will likely be corrupt.");
                }

                if (dataOffset != input.Position)
                {
                    Console.WriteLine("[warning] Data offset inconsistent! Files will likely be corrupt.");
                }

                long current = 0;
                long total = files.Count;
                var padding = total.ToString(CultureInfo.InvariantCulture).Length;

                foreach (var file in files)
                {
                    current++;

                    var name = file.Name;
                    var dir = file.Parent;
                    while (dir != null && dir.Name != null)
                    {
                        name = Path.Combine(dir.Name, name);
                        dir = dir.Parent;
                    }

                    var entryPath = Path.Combine(outputPath, name);
                    if (overwriteFiles == false && File.Exists(entryPath) == true)
                    {
                        continue;
                    }

                    if (verbose == true)
                    {
                        Console.WriteLine(
                            "[{0}/{1}] {2}",
                            current.ToString(CultureInfo.InvariantCulture).PadLeft(padding),
                            total,
                            name);
                    }

                    var entryDirectory = Path.GetDirectoryName(entryPath);
                    if (entryDirectory != null)
                    {
                        Directory.CreateDirectory(entryDirectory);
                    }

                    using (var output = File.Create(entryPath))
                    {
                        input.Seek(dataOffset + file.Offset, SeekOrigin.Begin);
                        output.WriteFromStream(input, file.Size);
                    }
                }
            }
        }
    }
}
