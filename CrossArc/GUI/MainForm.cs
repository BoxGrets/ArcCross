﻿using ArcCross;
using CrossArc.GUI.Nodes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CrossArc.GUI
{
    public partial class MainForm : Form
    {
        public string FilePath;
        public int Version;

        public static int SelectedRegion
        {
            get => int.Parse(ConfigurationManager.AppSettings["Region"]);
            set
            {
                var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                config.AppSettings.Settings["Region"].Value = value.ToString();
                config.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection("appSettings");
            }
        }

        public static Arc ArcFile;

        public static ContextMenu NodeContextMenu;

        private GuiNode rootNode;

        private BackgroundWorker searchWorker;

        private Regex regexPattern = null;

        private bool searchOffset = false;

        private Func<string, BaseNode, bool> searchCallback { get; set; }

        public Dictionary<string, FileInformation> pathToFileInfomation = new Dictionary<string, FileInformation>();

        public MainForm()
        {
            InitializeComponent();

            fileTreeView.BeforeExpand += folderTree_BeforeExpand;

            fileTreeView.NodeMouseClick += (sender, args) => fileTreeView.SelectedNode = args.Node;

            fileTreeView.HideSelection = false;

            fileTreeView.ImageList = new ImageList();
            fileTreeView.ImageList.Images.Add("folder", Properties.Resources.folder);
            fileTreeView.ImageList.Images.Add("file", Properties.Resources.file);

            exportFileSystemToXMLToolStripMenuItem.Enabled = false;
            exportFileSystemToTXTToolStripMenuItem.Enabled = false;

            comboBox1.SelectedIndex = SelectedRegion;

            comboBox1.SelectedIndexChanged += (sender, args) =>
            {
                SelectedRegion = comboBox1.SelectedIndex;
                treeView1_AfterSelect(null, null);
            };

            NodeContextMenu = new ContextMenu();

            {
                MenuItem item = new MenuItem("Extract file(s)");
                item.Click += ExtractFile;
                NodeContextMenu.MenuItems.Add(item);
            }
            {
                MenuItem item = new MenuItem("Extract file(s) (Compressed)");
                item.Click += ExtractFileComp;
                NodeContextMenu.MenuItems.Add(item);
            }
            {
                MenuItem item = new MenuItem("Extract file(s) (With Offset)");
                item.Click += ExtractFileOffset;
                NodeContextMenu.MenuItems.Add(item);
            }
            {
                MenuItem item = new MenuItem("Extract file(s) (Compressed, With Offset)");
                item.Click += ExtractFileCompOffset;
                NodeContextMenu.MenuItems.Add(item);
            }

            searchOffset = searchOffsetCheckBox.Checked;

            if (searchOffset)
                searchCallback = SearchCheckOffset;
            else
                searchCallback = SearchCheckPath;
        }

        private void ExtractFile(object sender, EventArgs args)
        {
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n && n.Base is FileNode file)
            {
                ProgressBar bar = new ProgressBar();
                bar.Show();
                bar.Extract(new[] { file });
            }
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n2 && n2.Base is FolderNode folder)
            {
                ProgressBar bar = new ProgressBar();
                bar.Show();
                bar.Extract(folder.GetAllFiles());
            }
        }

        private void ExtractFileComp(object sender, EventArgs args)
        {
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n && n.Base is FileNode file)
            {
                ProgressBar bar = new ProgressBar { DecompressFiles = false };
                bar.Show();
                bar.Extract(new[] { file });
            }
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n2 && n2.Base is FolderNode folder)
            {
                ProgressBar bar = new ProgressBar { DecompressFiles = false };
                bar.Show();
                bar.Extract(folder.GetAllFiles());
            }
        }

        private void ExtractFileOffset(object sender, EventArgs args)
        {
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n && n.Base is FileNode file)
            {
                ProgressBar bar = new ProgressBar();
                bar.UseOffsetName = true;
                bar.Show();
                bar.Extract(new[] { file });
            }
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n2 && n2.Base is FolderNode folder)
            {
                ProgressBar bar = new ProgressBar();
                bar.UseOffsetName = true;
                bar.Show();
                bar.Extract(folder.GetAllFiles());
            }
        }

        private void ExtractFileCompOffset(object sender, EventArgs args)
        {
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n && n.Base is FileNode file)
            {
                ProgressBar bar = new ProgressBar();
                bar.UseOffsetName = true;
                bar.DecompressFiles = false;
                bar.Show();
                bar.Extract(new[] { file });
            }
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n2 && n2.Base is FolderNode folder)
            {
                ProgressBar bar = new ProgressBar();
                bar.UseOffsetName = true;
                bar.DecompressFiles = false;
                bar.Show();
                bar.Extract(folder.GetAllFiles());
            }
        }

        private void folderTree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node is GuiNode n)
                n.BeforeExpand();
        }

        private void openARCToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var initHashes = Task.Run(() =>
            {
                if (!HashDict.Initialized)
                    HashDict.Init();
            });

            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.FileName = "data.arc";
                d.Filter += "Smash Ultimate ARC|*.arc";
                if (d.ShowDialog() == DialogResult.OK)
                {
                    Cursor.Current = Cursors.WaitCursor;

                    // Make sure the hashes are done before using them.
                    initHashes.Wait();
                    ArcFile = new Arc(d.FileName);

                    fileTreeView.Nodes.Clear();
                    rootNode = new GuiNode(FileSystem.CreateFileTreeGetRoot(ArcFile.FilePaths, ArcFile.StreamFilePaths));
                    fileTreeView.Nodes.Add(rootNode);

                    Cursor.Current = Cursors.Arrow;

                    // update arc version label
                    label1.Text = "Arc Version: " + ArcFile.Version.ToString("X");

                    // enable controls that can only be accessed when the arc is loaded
                    updateHashesToolStripMenuItem.Enabled = false;
                    exportFileSystemToXMLToolStripMenuItem.Enabled = true;
                    exportFileSystemToTXTToolStripMenuItem.Enabled = true;

                    Version = ArcFile.Version;
                    FilePath = d.FileName;
                }
            }
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            arcFilePropertyGrid.SelectedObject = null;
            if (fileTreeView.SelectedNode != null && fileTreeView.SelectedNode is GuiNode n && n.Base is FileNode file)
            {
                arcFilePropertyGrid.SelectedObject = file.FileInformation;
            }
            arcFilePropertyGrid.ExpandAllGridItems();
        }

        private async void updateHashesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var dl = MessageBox.Show("Download the latest archive hashes from github?", "Update Hashes.txt", MessageBoxButtons.YesNo);

            if (dl == DialogResult.Yes)
            {
                // Disable the tool strip to prevent opening another arc or hashes before the file has finished downloading.
                menuStrip1.Enabled = false;

                bool success = true;
                try
                {
                    await DownloadHashesAsync("Hashes_new.txt");
                }
                catch (WebException)
                {
                    success = false;
                }

                if (success)
                {
                    File.Delete("Hashes.txt");
                    File.Move("Hashes_new.txt", "Hashes.txt");
                    File.Delete("Hashes_new.txt");
                }

                await Task.Run(() =>
                {
                    // Refresh the hash dictionary.
                    HashDict.Unload();
                    HashDict.Init();
                });

                menuStrip1.Enabled = true;
            }
        }

        private async Task DownloadHashesAsync(string path)
        {
            using (var client = new WebClient())
            {
                await client.DownloadFileTaskAsync("https://github.com/ultimate-research/archive-hashes/raw/master/Hashes", path);
            }
        }

        private void searchBox_TextChanged(object sender, EventArgs e)
        {
            if (ArcFile == null || !ArcFile.Initialized || rootNode == null)
                return;

            // Cancel previous search
            if (searchWorker != null)
            {
                searchWorker.CancelAsync();
                searchWorker.Dispose();
                searchWorker = null;
            }
            fileTreeView.Nodes.Clear();

            if (searchBox.Text != "")
            {
                SetRegexPattern();

                searchWorker = new BackgroundWorker();
                searchWorker.DoWork += Search;
                searchWorker.ProgressChanged += AddNode;
                searchWorker.WorkerSupportsCancellation = true;
                searchWorker.WorkerReportsProgress = true;
                searchWorker.RunWorkerAsync();
                searchLabel.Visible = true;
            }
            else
            {
                fileTreeView.Nodes.Add(rootNode);
                searchLabel.Visible = false;
            }
        }

        private void searchOffsetCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            searchOffset = searchOffsetCheckBox.Checked;

            if (searchOffset)
                searchCallback = SearchCheckOffset;
            else
                searchCallback = SearchCheckPath;

            //trigger a re-search
            searchBox_TextChanged(null, null);
        }

        private void SetRegexPattern()
        {
            regexPattern = new Regex("^root/"
                + Regex.Escape(searchBox.Text)
                .Replace(@"\?", ".")
                .Replace(@"\*", ".*")
                + "$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase
            );
        }

        private bool SearchCheckPath(string path, BaseNode node)
        {
            return regexPattern.IsMatch(node.FullPath);
        }

        private bool SearchCheckOffset(string offsetStr, BaseNode node)
        {
            return node is FileNode file &&
                offsetStr.Length >= 3 &&
                offsetStr.StartsWith("0x") &&
                long.TryParse(offsetStr.Remove(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) &&
                file.FileInformation.Offset == value;
        }

        private void AddNode(object sender, ProgressChangedEventArgs args)
        {
            if (searchWorker != null)
            {
                if (args.ProgressPercentage == 100)
                {
                    System.Diagnostics.Debug.WriteLine("Done Searching");
                    searchLabel.Visible = false;
                }
                else
                {
                    // TODO: a potentially better way to implement search results is to
                    // apply a filter over the nodes. However, this requires changing
                    // GUINode.cs, since by default it displays every subnode
                    fileTreeView.Nodes.Add(new GuiNode((BaseNode)args.UserState));
                }
            }
        }

        private void Search(object sender, DoWorkEventArgs e)
        {
            Queue<BaseNode> toSearch = new Queue<BaseNode>();
            toSearch.Enqueue(rootNode.Base);

            bool interrupted = false;

            var key = searchBox.Text;
            if (key == "0")
                return;

            while (toSearch.Count > 0)
            {
                if (searchBox != null && key != searchBox.Text
                    || searchWorker == null
                    || searchWorker.CancellationPending)
                {
                    interrupted = true;
                    break;
                }

                var s = toSearch.Dequeue();

                // TODO: (Optimization idea)
                // if you do a search like "fighter/*.prc", the program should know that
                // it is impossible to find such a path in "sound", or "effect", etc.
                // It may be necessary to remove regex implementation for it.
                if (searchCallback(key, s))
                {
                    searchWorker.ReportProgress(0, s);
                }
                else
                {
                    foreach (var b in s.SubNodes)
                    {
                        toSearch.Enqueue(b);
                    }
                }
            }

            if (!interrupted)
                searchWorker.ReportProgress(100, null);
        }

        private void exportFileSystemToXMLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportFileSystemXml();
        }

        private void exportFileSystemToCsvToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportFileSystemCsv();
        }

        private void ExportFileSystemCsv()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV (*.csv)|*.csv";

                if (d.ShowDialog() == DialogResult.OK)
                {
                    rootNode.Base.WriteToFileCsv(d.FileName);
                }
            }
        }

        private void ExportFileSystemXml()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "XML (*.xml)|*.xml";

                if (d.ShowDialog() == DialogResult.OK)
                {
                    rootNode.Base.WriteToFileXML(d.FileName);
                }
            }
        }
    }
}
