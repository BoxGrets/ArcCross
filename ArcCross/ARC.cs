﻿using ArcCross.StructsV1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Zstandard.Net;

namespace ArcCross
{
    public class Arc
    {
        public int Version { get; set; }

        private string filePath;

        // Get only to ensure the file paths are only initialized once.
        public IList<string> FilePaths { get; }
        public IList<string> StreamFilePaths { get; }

        private const ulong Magic = 0xABCDEF9876543210;

        // TODO: Move file header related fields to a separate class/struct.
        private _sArcHeader header;

        // stream
        private _sStreamUnk[] streamUnk;
        private _sStreamHashToName[] streamHashToName;
        private _sStreamNameToHash[] streamNameToHash;
        private _sStreamIndexToOffset[] streamIndexToFile;
        private _sStreamOffset[] streamOffsets;

        // file system
        private _sFileSystemHeader fsHeader;
        private byte[] regionalBytes;
        private _sStreamHeader streamHeader;

        private _sFileInformationPath[] fileInfoPath;
        private _sFileInformationIndex[] fileInfoIndex;
        private _sFileInformationSubIndex[] fileInfoSubIndex;
        private _sFileInformationV2[] fileInfoV2;

        private _sSubFileInfo[] subFiles;

        private _sFileInformationUnknownTable[] fileInfoUnknownTable;
        private _sHashIndexGroup[] filePathToIndexHashGroup;

        // Directory information
        private _sHashIndexGroup[] directoryHashGroup;
        private _sDirectoryList[] directoryList;
        private _sDirectoryOffset[] directoryOffsets;
        private _sHashIndexGroup[] directoryChildHashGroup;

        // V1 arc only
        private _sFileInformationV1[] fileInfoV1;

        // handling
        public bool Initialized { get; internal set; }

        private Dictionary<string, _sFileInformationV1> pathToFileInfoV1 = new Dictionary<string, _sFileInformationV1>();
        private Dictionary<string, _sFileInformationV2> pathToFileInfo = new Dictionary<string, _sFileInformationV2>();
        private Dictionary<uint, _sStreamNameToHash> pathCrc32ToStreamInfo = new Dictionary<uint, _sStreamNameToHash>();
        private IDictionary<_sSubFileInfo, List<_sFileInformationV2>> SharedLookup = new Dictionary<_sSubFileInfo, List<_sFileInformationV2>>();

        /// <summary>
        /// Initializes a new instance of the <see cref="Arc"/> class and initializes the file system from the specified path.
        /// </summary>
        /// <param name="arcFilePath"></param>
        public Arc(string arcFilePath)
        {
            InitFileSystem(arcFilePath);

            FilePaths = GetFileList();
            StreamFilePaths = GetStreamFileList();

            InitializeSharedLookup();
            InitializePathToFileInfo();
        }

        private void InitFileSystem(string arcFilePath)
        {
            filePath = arcFilePath;

            using (ExtBinaryReader reader = new ExtBinaryReader(new FileStream(arcFilePath, FileMode.Open)))
            {
                Initialized = Init(reader);
            }
        }

        private void InitializePathToFileInfo()
        {
            pathCrc32ToStreamInfo = new Dictionary<uint, _sStreamNameToHash>(streamNameToHash.Length);
            foreach (var v in streamNameToHash)
                pathCrc32ToStreamInfo.Add(v.Hash, v);
            
            // Ignore duplicate paths.
            if (Version == 0x00010000)
            {
                pathToFileInfoV1 = new Dictionary<string, _sFileInformationV1>(FilePaths.Count);
                for (int i = 0; i < FilePaths.Count; i++)
                {
                    pathToFileInfoV1[FilePaths[i]] =  fileInfoV1[i];
                }
            }
            else
            {
                pathToFileInfo = new Dictionary<string, _sFileInformationV2>(FilePaths.Count);
                for (int i = 0; i < FilePaths.Count; i++)
                {
                    pathToFileInfo[FilePaths[i]] = fileInfoV2[i];
                }
            }
        }

        private void InitializeSharedLookup()
        {
            foreach (var fi in fileInfoV2)
            {
                GetSubInfo(fi, out var subFileInfo, out var _);

                if (!SharedLookup.ContainsKey(subFileInfo)) SharedLookup[subFileInfo] = new List<_sFileInformationV2>();

                var fiv = SharedLookup[subFileInfo];

                if (!fiv.Select(f => f.PathIndex).Contains(fi.PathIndex)) // prevent duplicate files with same path
                    fiv.Add(fi);
            }
        }

        /// <summary>
        /// Initializes arc from binary reader
        /// </summary>
        /// <param name="reader"></param>
        private bool Init(ExtBinaryReader reader)
        {
            if (!HashDict.Initialized)
                HashDict.Init();

            if (reader.BaseStream.Length < Marshal.SizeOf<_sArcHeader>())
                return false;

            header = reader.ReadType<_sArcHeader>();

            if (header.Magic != Magic)
                throw new InvalidDataException("ARC magic does not match");

            reader.BaseStream.Seek(header.FileSystemOffset, SeekOrigin.Begin);
            if (header.FileDataOffset < 0x8824AF68)
            {
                Version = 0x00010000;
                ReadFileSystemV1(ReadCompressedTable(reader));
            }
            else
            {
                ReadFileSystem(ReadCompressedTable(reader));
            }

            //don't read yet since rebuild doesn't work correctly anyway
            //reader.BaseStream.Seek(header.FileSystemSearchOffset, SeekOrigin.Begin);
            //ReadSearchTable(ReadCompressedTable(reader));

            return true;
        }

        /// <summary>
        /// Reads file system table from Arc
        /// </summary>
        /// <param name="fileSystemTable"></param>
        private void ReadFileSystem(byte[] fileSystemTable)
        {
            using (ExtBinaryReader reader = new ExtBinaryReader(new MemoryStream(fileSystemTable)))
            {
                fsHeader = reader.ReadType<_sFileSystemHeader>();

                uint extraFolder = 0;
                uint extraCount = 0;
                uint extraCount2 = 0;
                uint extraSubCount = 0;

                if (fileSystemTable.Length >= 0x2992DD4)
                {
                    // Version 3+
                    Version = reader.ReadInt32();

                    extraFolder = reader.ReadUInt32(); 
                    extraCount = reader.ReadUInt32();

                    reader.ReadBytes(8);  // some extra thing :thinking
                    extraCount2 = reader.ReadUInt32();
                    extraSubCount = reader.ReadUInt32();
                }
                else
                {
                    Version = 0x00020000;
                    reader.BaseStream.Seek(0x3C, SeekOrigin.Begin);
                }

                // skip this for now
                regionalBytes = reader.ReadBytes(0xE * 12);

                // Streams
                streamHeader = reader.ReadType<_sStreamHeader>();

                streamUnk = reader.ReadType<_sStreamUnk>(streamHeader.UnkCount);
                
                streamHashToName = reader.ReadType<_sStreamHashToName>(streamHeader.StreamHashCount);
                
                streamNameToHash = reader.ReadType<_sStreamNameToHash>(streamHeader.StreamHashCount);

                streamIndexToFile = reader.ReadType<_sStreamIndexToOffset>(streamHeader.StreamIndexToOffsetCount);

                streamOffsets = reader.ReadType<_sStreamOffset>(streamHeader.StreamOffsetCount);

                Console.WriteLine(reader.BaseStream.Position.ToString("X")); 

                // Unknown
                uint unkCount1 = reader.ReadUInt32();
                uint unkCount2 = reader.ReadUInt32();
                fileInfoUnknownTable = reader.ReadType<_sFileInformationUnknownTable>(unkCount2);
                filePathToIndexHashGroup = reader.ReadType<_sHashIndexGroup>(unkCount1);

                // FileTables
                
                fileInfoPath = reader.ReadType<_sFileInformationPath>(fsHeader.FileInformationPathCount);

                fileInfoIndex = reader.ReadType<_sFileInformationIndex>(fsHeader.FileInformationIndexCount);

                // directory tables

                // directory hashes by length and index to directory probably 0x6000 something
                Console.WriteLine(reader.BaseStream.Position.ToString("X"));
                directoryHashGroup = reader.ReadType<_sHashIndexGroup>(fsHeader.DirectoryCount);
                
                directoryList = reader.ReadType<_sDirectoryList>(fsHeader.DirectoryCount);
                
                directoryOffsets = reader.ReadType<_sDirectoryOffset>(fsHeader.DirectoryOffsetCount1 + fsHeader.DirectoryOffsetCount2 + extraFolder);
                
                directoryChildHashGroup = reader.ReadType<_sHashIndexGroup>(fsHeader.DirectoryHashSearchCount);

                // file information tables
                Console.WriteLine(reader.BaseStream.Position.ToString("X"));
                fileInfoV2 = reader.ReadType<_sFileInformationV2>(fsHeader.FileInformationCount + fsHeader.SubFileCount2 + extraCount);
                
                fileInfoSubIndex = reader.ReadType<_sFileInformationSubIndex>(fsHeader.FileInformationSubIndexCount + fsHeader.SubFileCount2 + extraCount2);
                
                subFiles = reader.ReadType<_sSubFileInfo>(fsHeader.SubFileCount + fsHeader.SubFileCount2 + extraSubCount);
                Console.WriteLine("End:" + reader.BaseStream.Position.ToString("X"));
            }
        }

        public void WriteFileSystem(string filename)
        {
            MemoryStream stream = new MemoryStream();
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(GetBytes(fsHeader));

                writer.Write(regionalBytes);

                writer.Write(GetBytes(streamHeader));

                writer.Write(GetBytes(streamUnk));

                writer.Write(GetBytes(streamHashToName));

                writer.Write(GetBytes(streamNameToHash));

                writer.Write(GetBytes(streamIndexToFile));

                writer.Write(GetBytes(streamOffsets));

                writer.Write(filePathToIndexHashGroup.Length);
                writer.Write(fileInfoUnknownTable.Length);

                writer.Write(GetBytes(fileInfoUnknownTable));

                writer.Write(GetBytes(filePathToIndexHashGroup));

                writer.Write(GetBytes(fileInfoPath));

                writer.Write(GetBytes(fileInfoIndex));

                writer.Write(GetBytes(directoryHashGroup));

                writer.Write(GetBytes(directoryList));
                writer.Write(GetBytes(directoryOffsets));
                writer.Write(GetBytes(directoryChildHashGroup));

                // file information tables

                writer.Write(GetBytes(fileInfoV2));
                writer.Write(GetBytes(fileInfoSubIndex));
                writer.Write(GetBytes(subFiles));

            }

            byte[] data = stream.ToArray();
            stream.Dispose();
            using (var memoryStream = new MemoryStream())
            {
                using (var zstream = new ZstandardStream(memoryStream, 20, true))
                {
                    zstream.Write(data, 0, data.Length);
                }
                File.WriteAllBytes("tablecompressed.bin", memoryStream.ToArray());
            }
        }

        private static byte[] GetBytes<T>(T[] str)
        {
            int size = Marshal.SizeOf<T>();
            byte[] arr = new byte[size * str.Length];

            for(int i = 0; i < str.Length; i++)
            {
                byte[] bytes = GetBytes(str[i]);
                Array.Copy(bytes, 0, arr, i * size, bytes.Length);
            }
            return arr;
        }

        private static byte[] GetBytes<T>(T str)
        {
            int size = Marshal.SizeOf<T>();
            byte[] arr = new byte[size];

            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(str, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }

        /// <summary>
        /// Reads the search table information for V2 Arc
        /// </summary>
        /// <param name="searchTable"></param>
        private void ReadSearchTable(byte[] searchTable)
        {
            using (ExtBinaryReader r = new ExtBinaryReader(new MemoryStream(searchTable)))
            {
                var header = r.ReadType<_sSearchHashHeader>();

                // paths
                var actualPathHashSection = r.ReadType<_sHashIndexGroup>(header.FolderLengthHashCount);

                int maxFolderIndex = 0;
                foreach(var section in actualPathHashSection)
                    maxFolderIndex = Math.Max(maxFolderIndex, section.index >> 8);
                Console.WriteLine("Max FolderIndex " + maxFolderIndex.ToString("X"));

                // path lookup group
                var unkHashGroup = r.ReadType<_sHashGroup>(header.FolderLengthHashCount);
                Console.WriteLine("End at: " + r.BaseStream.Position.ToString("X"));

                // file paths
                var actualFullPath = r.ReadType<_sHashIndexGroup>(header.SomeCount3);
                Console.WriteLine("End at: " + r.BaseStream.Position.ToString("X"));

                // index to look up pathgroup? why they needed a separate index who knows...
                var indexTable = r.ReadType<int>(header.SomeCount3);
                Console.WriteLine("End at: " + r.BaseStream.Position.ToString("X"));

                // file path lookup group
                var pathNameHashGroupLookup = r.ReadType<_sHashGroup>(header.SomeCount4);
                Console.WriteLine("End at: " + r.BaseStream.Position.ToString("X"));

                int maxIntTableValue = 0;
                foreach (var g in unkHashGroup)
                {
                    maxIntTableValue = Math.Max((int)g.ExtensionHash, maxIntTableValue);
                    //Console.WriteLine(HashDict.GetString(g.Hash));
                }
                Console.WriteLine("Max table value: " + maxIntTableValue.ToString("X"));
            }
        }

        /// <summary>
        /// Arc versions other than 1.0 have compressed tables
        /// </summary>
        /// <param name="reader"></param>
        /// <returns></returns>
        private static byte[] ReadCompressedTable(ExtBinaryReader reader)
        {
            var compHeader = reader.ReadType<_sCompressedTableHeader>();

            if(compHeader.DataOffset > 0x10)
            {
                var tableStart = reader.BaseStream.Position - 0x10;
                reader.BaseStream.Seek(tableStart, SeekOrigin.Begin);
                var size = reader.ReadInt32();
                reader.BaseStream.Seek(tableStart, SeekOrigin.Begin);
                return reader.ReadBytes(size);
            }
            else
            if(compHeader.DataOffset == 0x10)
            {
                return reader.ReadZstdCompressed(compHeader.CompressedSize);
            }
            else
            {
                return reader.ReadBytes(compHeader.CompressedSize);
            }
        }

        public List<string> GetSharedFiles(string path, int region = 0)
        {
            List<string> shared = new List<string>();
            if (GetArcFileInfoV2(path, out var fileInfo))
            {
                GetSubInfo(fileInfo, out var subFile, out _, region);

                var fiv = SharedLookup[subFile];

                foreach (var fi in fiv)
                    shared.Add(GetFilePathFromInfo(fi));

                shared.Sort();
            }

            return shared;
        }

        public long MaxOffset()
        {
            long max = 0;
            foreach(var fi in fileInfoV2)
            {
                var path = fileInfoPath[fi.PathIndex];
                var subIndex = fileInfoSubIndex[fi.SubIndexIndex];

                var subFile = subFiles[subIndex.SubFileIndex];

                var directoryOffset = directoryOffsets[subIndex.DirectoryOffsetIndex];
                max = Math.Max(max, header.FileDataOffset + directoryOffset.Offset + (subFile.Offset<<2));
                
                if (header.FileDataOffset + directoryOffset.Offset + (subFile.Offset << 2) > header.FileDataOffset2)
                    Console.WriteLine(HashDict.GetString(path.FileName));
            }
            return max;
        }

        public void PrintDirectoryInfo(string directory)
        {
            uint hash = CRC32.Crc32C(directory);
            int count = -1;
            foreach(var dir in directoryHashGroup)
            {
                //Console.WriteLine(HashDict.GetString(dir.Hash) + " " + dir.index.ToString("X"));
                count++;
                if(dir.Hash == hash)
                {
                    var direct = directoryList[dir.index >> 8];
                    Console.WriteLine(directory);

                    Console.WriteLine("Sub Folders:");
                    for(int i = direct.ChildDirectoryStartIndex; i < direct.ChildDirectoryStartIndex + direct.ChildDirectoryCount; i++)
                    {
                        var hashGroup = directoryChildHashGroup[i];
                        Console.WriteLine("\t" + HashDict.GetString(hashGroup.Hash));
                    }

                    Console.WriteLine("Sub Files:");
                    for (int i = direct.FileInformationStartIndex; i < direct.FileInformationStartIndex + direct.FileInformationCount; i++)
                    {
                        var fileinfo = fileInfoV2[i];

                        var fileindex = fileInfoIndex[fileinfo.IndexIndex];

                        //redirect
                        if ((fileinfo.Flags & 0x00000010) == 0x10)
                        {
                            fileinfo = fileInfoV2[fileindex.FileInformationIndex];
                        }

                        var path = fileInfoPath[fileinfo.PathIndex];
                        var subindex = fileInfoSubIndex[fileinfo.SubIndexIndex];

                        var subfile = subFiles[subindex.SubFileIndex];
                        var directoryOffset = directoryOffsets[subindex.DirectoryOffsetIndex];
                        
                        //regional
                        if ((fileinfo.Flags & 0x00008000) == 0x8000)
                        {
                            subindex = fileInfoSubIndex[fileinfo.SubIndexIndex+2];
                            subfile = subFiles[subindex.SubFileIndex];
                            directoryOffset = directoryOffsets[subindex.DirectoryOffsetIndex];
                        }

                        Console.WriteLine("\t" + HashDict.GetString(path.Parent) + HashDict.GetString(path.FileName));
                        Console.WriteLine("\t\t" + i + " " + fileinfo.SubIndexIndex + " " + path.DirectoryIndex + " " + fileinfo.IndexIndex + " " + (header.FileDataOffset + directoryOffset.Offset + (subfile.Offset<<2)).ToString("X") + " " + subfile.CompSize.ToString("X") + " " + subfile.DecompSize.ToString("X") + " " + subfile.Flags.ToString("X") + " " + fileinfo.Flags.ToString("X") + " " + subindex.SubFileIndex + " " + directoryOffset.SubDataStartIndex + " " + subindex.FileInformationIndexAndFlag.ToString("X"));
                    }
                    break;
                }
            }
        }


        private List<string> GetFileList()
        {
            if (Version == 0x00010000)
                return GetFileListV1();
            else
                return GetFileListV2();
        }

        /// <summary>
        /// Returns an unorganized list of the files in the arc, excluding stream files.
        /// </summary>
        /// <returns>An unorganized list of the files in the arc, excluding stream files.</returns>
        public List<string> GetFileListV1()
        {
            var files = new List<string>(fileInfoV1.Length);

            foreach (var fileInfo in fileInfoV1)
            {
                string pathString = HashDict.GetString(fileInfo.Parent, (int)(fileInfo.Unk5 & 0xFF));

                string filename = HashDict.GetString(fileInfo.Hash2, (int)(fileInfo.Unk6 & 0xFF));
                if (filename.StartsWith("0x"))
                    filename += HashDict.GetString(fileInfo.Extension);

                files.Add(pathString + filename);
            }

            return files;
        }

        private List<string> GetFileListV2() => fileInfoV2.Select(f => GetFilePathFromInfo(f)).ToList();

        /// <summary>
        /// returns the decompressed file
        /// </summary>
        /// <param name="filepath"></param>
        /// <param name="regionIndex"></param>
        /// <returns></returns>
        public byte[] GetFile(string filepath, int regionIndex = 0)
        {
            GetFileInformation(filepath, out long offset, out uint compSize, out uint decompSize, out bool regional, regionIndex);

            if(decompSize > 0 && decompSize != compSize)
            {
                var decompressed = ExtBinaryReader.DecompressZstd(GetSection(offset, compSize));
                if (decompressed.Length != decompSize)
                    throw new InvalidDataException("Error decompressing file");
                return decompressed;
            }
            return GetSection(offset, compSize);
        }

        /// <summary>
        /// returns the compressed version of the file (if it is compressed)
        /// </summary>
        /// <param name="filepath"></param>
        /// <param name="regionIndex"></param>
        /// <returns></returns>
        public byte[] GetFileCompressed(string filepath, int regionIndex = 0)
        {
            GetFileInformation(filepath, out long offset, out uint compSize, out uint decompSize, out bool regional, regionIndex);
            return GetSection(offset, compSize);
        }

        /// <summary>
        /// returns a section of the arc
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="size"></param>
        /// <returns></returns>
        private byte[] GetSection(long offset, uint size)
        {
            if (!Initialized)
                return new byte[0];
            byte[] data;
            using (BinaryReader reader = new BinaryReader(new FileStream(filePath, FileMode.Open)))
            {
                reader.BaseStream.Position = offset;
                data = reader.ReadBytes((int)size);
            }
            return data;
        }

        private bool GetArcFileInfoV2(string path, out _sFileInformationV2 fileOut)
        {
            if (pathToFileInfo.ContainsKey(path))
            {
                fileOut = pathToFileInfo[path];
                return true;
            }
            fileOut = default(_sFileInformationV2);
            return false;
        }

        private string GetFilePathFromInfo(_sFileInformationV2 file)
        {
            var path = fileInfoPath[file.PathIndex]; 

            string pathString = HashDict.GetString(path.Parent, (int)(path.Unk5 & 0xFF));
            string filename = HashDict.GetString(path.FileName, (int)(path.Unk6 & 0xFF));
            if (filename.StartsWith("0x"))
                filename += HashDict.GetString(path.Extension);

            return pathString + filename;
        }

        /// <summary>
        /// gets file information from the file's path
        /// </summary>
        /// <param name="filepath"></param>
        /// <param name="offset"></param>
        /// <param name="compSize"></param>
        /// <param name="decompSize"></param>
        /// <param name="regionIndex"></param>
        public void GetFileInformation(string filepath, out long offset, out uint compSize, out uint decompSize, out bool regional, int regionIndex = 0)
        {
            offset = 0;
            compSize = 0;
            decompSize = 0;
            regional = false;

            if ((Version != 0x00010000 && !pathToFileInfo.ContainsKey(filepath)) ||
                (Version == 0x00010000 && !pathToFileInfoV1.ContainsKey(filepath)))
            {   //
                // check for stream file
                var strcrc = CRC32.Crc32C(filepath);
                if(pathCrc32ToStreamInfo.ContainsKey(strcrc))
                {
                    var fileinfo = pathCrc32ToStreamInfo[strcrc];

                    if (fileinfo.Flags == 1 || fileinfo.Flags == 2)
                    {
                        if (fileinfo.Flags == 2 && regionIndex > 5)
                            regionIndex = 0;

                        var streamindex = streamIndexToFile[(fileinfo.NameIndex >> 8) + regionIndex].FileIndex;
                        var offsetinfo = streamOffsets[streamindex];
                        offset = offsetinfo.Offset;
                        compSize = (uint)offsetinfo.Size;
                        decompSize = (uint)offsetinfo.Size;
                        regional = true;
                    }
                    else
                    {
                        var streamindex = streamIndexToFile[fileinfo.NameIndex >> 8].FileIndex;
                        var offsetinfo = streamOffsets[streamindex];
                        offset = offsetinfo.Offset;
                        compSize = (uint)offsetinfo.Size;
                        decompSize = (uint)offsetinfo.Size;
                    }
                    return;
                }

                return;
            }
            if (IsRegional(filepath))
                regional = true;

            if(Version == 0x00010000)
                GetFileInformation(pathToFileInfoV1[filepath], out offset, out compSize, out decompSize, regionIndex);
            else
                GetFileInformation(pathToFileInfo[filepath], out offset, out compSize, out decompSize, regionIndex);
        }

        public void GetSubInfo(_sFileInformationV2 fileinfo, out _sSubFileInfo subFile, out _sDirectoryOffset directoryOffset, int regionIndex = 0)
        {
            var fileIndex = fileInfoIndex[fileinfo.IndexIndex];

            //redirect
            if ((fileinfo.Flags & 0x00000010) == 0x10)
            {
                fileinfo = fileInfoV2[fileIndex.FileInformationIndex];
            }

            var path = fileInfoPath[fileinfo.PathIndex];
            var subIndex = fileInfoSubIndex[fileinfo.SubIndexIndex];

            subFile = subFiles[subIndex.SubFileIndex];
            directoryOffset = directoryOffsets[subIndex.DirectoryOffsetIndex];

            //regional
            if ((fileinfo.Flags & 0x00008000) == 0x8000)
            {
                subIndex = fileInfoSubIndex[fileinfo.SubIndexIndex + 1 + regionIndex];
                subFile = subFiles[subIndex.SubFileIndex];
                directoryOffset = directoryOffsets[subIndex.DirectoryOffsetIndex];
            }

        }

        /// <summary>
        /// Gets the information for a given file info
        /// </summary>
        /// <param name="fileinfo"></param>
        /// <param name="offset"></param>
        /// <param name="compSize"></param>
        /// <param name="decompSize"></param>
        /// <param name="regionIndex"></param>
        private void GetFileInformation(_sFileInformationV2 fileinfo, out long offset, out uint compSize, out uint decompSize, int regionIndex = 0)
        {
            GetSubInfo(fileinfo, out var subFile, out var directoryOffset, regionIndex);

            offset = (header.FileDataOffset + directoryOffset.Offset + (subFile.Offset << 2));
            compSize = subFile.CompSize;
            decompSize = subFile.DecompSize;
        }

        public bool IsRedirected(string path)
        {
            if (pathToFileInfoV1 != null && pathToFileInfoV1.ContainsKey(path))
                return ((pathToFileInfoV1[path].Flags & 0x00300000) == 0x00300000);
            if (pathToFileInfo != null && pathToFileInfo.ContainsKey(path))
                return (pathToFileInfo[path].Flags & 0x00000010) == 0x10;
            return false;
        }

        public bool IsRegional(string path)
        {
            if (pathToFileInfoV1 != null && pathToFileInfoV1.ContainsKey(path))
                return ((pathToFileInfoV1[path].FileTableFlag >> 8) > 0);
            if (pathToFileInfo != null && pathToFileInfo.ContainsKey(path))
                return ((pathToFileInfo[path].Flags & 0x00008000) == 0x8000);
            return false;
        }

        private List<string> GetStreamFileList()
        {
            var files = new List<string>(streamNameToHash.Length);

            foreach (var fileInfo in streamNameToHash)
            {
                files.Add(HashDict.GetString(fileInfo.Hash, (int)(fileInfo.NameIndex&0xFF)));
            }

            return files;
        }

        /// <summary>
        /// Parses filesystem from the 1.0.0 arc
        /// </summary>
        /// <param name="fileSystemTable"></param>
        private void ReadFileSystemV1(byte[] fileSystemTable)
        {
            using (ExtBinaryReader reader = new ExtBinaryReader(new MemoryStream(fileSystemTable)))
            {
                reader.BaseStream.Position = 0;
                var nodeHeader = reader.ReadType<_sFileSystemHeaderV1>();

                reader.BaseStream.Seek(0x68, SeekOrigin.Begin);

                // Hash Table
                reader.BaseStream.Seek(0x8 * nodeHeader.Part1Count, SeekOrigin.Current);

                // Hash Table 2
                System.Diagnostics.Debug.WriteLine("stream " + reader.BaseStream.Position.ToString("X"));
                streamNameToHash = reader.ReadType<_sStreamNameToHash>(nodeHeader.Part1Count);

                // Hash Table 3
                System.Diagnostics.Debug.WriteLine("stream " + reader.BaseStream.Position.ToString("X"));
                streamIndexToFile = reader.ReadType<_sStreamIndexToOffset>(nodeHeader.Part2Count);

                // stream offsets
                System.Diagnostics.Debug.WriteLine("stream " + reader.BaseStream.Position.ToString("X"));
                streamOffsets = reader.ReadType<_sStreamOffset>(nodeHeader.MusicFileCount);

                // Another Hash Table
                System.Diagnostics.Debug.WriteLine("RegionalHashList " + reader.BaseStream.Position.ToString("X"));
                reader.BaseStream.Seek(0xC * 0xE, SeekOrigin.Current);

                //folders

                System.Diagnostics.Debug.WriteLine("FolderHashes " + reader.BaseStream.Position.ToString("X"));
                directoryList = reader.ReadType<_sDirectoryList>(nodeHeader.FolderCount);

                //file offsets

                System.Diagnostics.Debug.WriteLine("fileoffsets " + reader.BaseStream.Position.ToString("X"));
                directoryOffsets = reader.ReadType<_sDirectoryOffset>(nodeHeader.FileCount1 + nodeHeader.FileCount2);
                //DirectoryOffsets_2 = reader.ReadType<_sDirectoryOffsets>(R, NodeHeader.FileCount2);

                System.Diagnostics.Debug.WriteLine("subfileoffset " + reader.BaseStream.Position.ToString("X"));
                directoryChildHashGroup = reader.ReadType<_sHashIndexGroup>(nodeHeader.HashFolderCount);

                System.Diagnostics.Debug.WriteLine("subfileoffset " + reader.BaseStream.Position.ToString("X"));
                fileInfoV1 = reader.ReadType<_sFileInformationV1>(nodeHeader.FileInformationCount);

                System.Diagnostics.Debug.WriteLine("subfileoffset " + reader.BaseStream.Position.ToString("X"));
                subFiles = reader.ReadType<_sSubFileInfo>(nodeHeader.SubFileCount + nodeHeader.SubFileCount2);
            }
        }

        private void GetFileInformation(_sFileInformationV1 fileInfo, out long offset, out uint compSize, out uint decompSize, int regionIndex = 0)
        {
            var subFile = subFiles[fileInfo.SubFile_Index];
            var dirIndex = directoryList[fileInfo.DirectoryIndex >> 8].FullPathHashLengthAndIndex >> 8;
            var directoryOffset = directoryOffsets[dirIndex];
            
            //redirect
            if ((fileInfo.Flags & 0x00300000) == 0x00300000)
            {
                GetFileInformation(fileInfoV1[subFile.Flags&0xFFFFFF], out offset, out compSize, out decompSize, regionIndex);
                return;
            }

            //regional
            if (IsRegional(fileInfo))
            {
                subFile = subFiles[(fileInfo.FileTableFlag >> 8) + regionIndex];
                directoryOffset = directoryOffsets[dirIndex + 1 + regionIndex];
            }

            offset = (header.FileDataOffset + directoryOffset.Offset + (subFile.Offset << 2));
            compSize = subFile.CompSize;
            decompSize = subFile.DecompSize;
        }

        private static bool IsRegional(_sFileInformationV1 info)
        {
            return (info.FileTableFlag >> 8) > 0;
        }
    }
}
