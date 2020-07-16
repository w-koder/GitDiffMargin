﻿using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Perforce.P4;
using Microsoft.VisualStudio.Shell.Interop;
using System.Diagnostics;

namespace GitDiffMargin.Git
{
    public class PerforceCommands : IGitCommands
    {
        public event EventHandler ConnectionChanged;

        private static PerforceCommands instance = null;

        private readonly IServiceProvider _serviceProvider;
        private Server _server;
        private Repository _repository;
        private Connection _connection;
        private string _perforceRoot;
        private bool _connected;
        private string _last_error;

        public static PerforceCommands GetInstance(IServiceProvider serviceProvider = null)
        {
            if (instance == null)
                instance = new PerforceCommands(serviceProvider);
            return instance;
        }

        // P4USER, P4PORT and P4CLIENT should be set. 

        private PerforceCommands(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;

            RefreshConnection();
        }

        private void Init()
        {
            if (_server == null)
            {
                _server = new Server(new ServerAddress(""));
            }

            if (_repository == null)
            {
                _repository = new Repository(_server);
            }
            _connection = _repository.Connection;

            if (!_connection.connectionEstablished())
            {
                _connection.UserName = "";
                _connection.Client = new Client();
                _connection.Client.Name = "";
                _connection.Connect(null);
            }
        }

        public bool Login(string password)
        {
            bool res = false;
            DisconnectImpl();
            Init();

            try
            {
                _connection.Login(password);
                res = true;
            }
            catch (P4Exception ex)
            {
                _last_error = ex.Message;
            }
            return res;
        }

        public void RefreshConnection()
        {
            RefreshConnectionImpl();
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Disconnect()
        {
            DisconnectImpl();
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RefreshConnectionImpl()
        {
            DisconnectImpl();
            Init();

            _perforceRoot = _repository.GetClientMetadata().Root;

            var error_msg = String.Format("Can't establish Perforce connection. P4USER, P4PORT and P4CLIENT perforce environment varialbes should be set. Login should be done. Currently thier values are: \nP4USER={0} \nP4PORT={1} \nP4CLIENT={2}",
                _connection.GetP4EnvironmentVar("P4USER"), _connection.GetP4EnvironmentVar("P4PORT"), _connection.GetP4EnvironmentVar("P4CLIENT"));

            if (!_connection.connectionEstablished() || _connection.GetActiveTicket() == null)
            {
                // TODO: close connection?
                DisconnectImpl();
                _last_error = error_msg;
                return;
            }

            if (_perforceRoot == null || !Directory.Exists(_perforceRoot))
            {
                DisconnectImpl();
                _last_error = error_msg + " \nError: \nWorkspace root is unset or doesn't exist";
                return;
            }

            // check user:
            // check ticket exists and valid. 
            var cmd = new P4Command(_connection, "login", true, new string[] { "-s" }); // p4 login -s get status of the ticket
            try
            {
                // P4Exception will be thrown in case user is not logged in
                cmd.Run();
            }
            catch (P4Exception ex)
            {
                // TODO: close connection?
                DisconnectImpl();
                _last_error = error_msg + " \nError: \n" + ex.Message;
                return;
            }
            // this ticket should belong to current user
            var output = cmd.TextOutput;
            if (cmd.TaggedOutput[0]["User"] != _connection.UserName)
            {
                // TODO: close connection?
                DisconnectImpl();
                _last_error = error_msg + " \nError: \nP4USER variable don't correspond to logged in user.";
                return;
            }

            _connected = true;
            _last_error = "";
        }

        public void DisconnectImpl()
        {
            _connected = false;
            if (_connection != null)
            {
                _connection.Disconnect();
            }
        }

        public bool Connected
        {
            get
            {
                return _connected;
            }
        }

        public IEnumerable<HunkRangeInfo> GetGitDiffFor(ITextDocument textDocument, ITextSnapshot snapshot)
        {
            if (!IsGitRepository(textDocument.FilePath))
                yield break;

            var depotPath = GetPerforcePath(textDocument.FilePath);

            IList<FileDiff> target = null;
            try
            {
                // get diff using p4 diff
                GetDepotFileDiffsCmdOptions opts = new GetDepotFileDiffsCmdOptions(GetDepotFileDiffsCmdFlags.Unified, 0, 0, "", "", "");
                IList<FileSpec> fsl = new List<FileSpec>();
                FileSpec fs = new FileSpec(new DepotPath(depotPath));
                fsl.Add(fs);
                target = _repository.GetFileDiffs(fsl, opts);
            }
            catch (P4Exception)
            {
                DisconnectImpl();
                yield break;
            }

            // TODO: implement comparison with yellow changes not "file on disk" vs "file in depot" but "file in RAM in VS" vs "file in depot"
            //var content = GetCompleteContent(textDocument, snapshot);
            //if (content == null) yield break;

            // TODO: after debugging remove the second and the third arguments from c'tor and next code, are they useless?
            var gitDiffParser = new GitDiffParser(target[0].Diff, 0, false);
            var hunkRangeInfos = gitDiffParser.Parse();

            foreach (var hunkRangeInfo in hunkRangeInfos)
            {
                yield return hunkRangeInfo;
            }
        }

        // Probably will be required to get content
        private static byte[] GetCompleteContent(ITextDocument textDocument, ITextSnapshot snapshot)
        {
            var currentText = snapshot.GetText();

            var content = textDocument.Encoding.GetBytes(currentText);

            var preamble = textDocument.Encoding.GetPreamble();
            if (preamble.Length == 0) return content;

            var completeContent = new byte[preamble.Length + content.Length];
            Buffer.BlockCopy(preamble, 0, completeContent, 0, preamble.Length);
            Buffer.BlockCopy(content, 0, completeContent, preamble.Length, content.Length);

            return completeContent;
        }

        public void StartExternalDiff(ITextDocument textDocument)
        {
            if (textDocument == null || string.IsNullOrEmpty(textDocument.FilePath))
                return;

            var filename = textDocument.FilePath;

            if (!IsGitRepository(filename))
                return;

            var depotPath = GetPerforcePath(filename);
            FileSpec fs = new FileSpec(new DepotPath(depotPath));

            var tempFileName = Path.GetTempFileName();
            var printOptions = new GetFileContentsCmdOptions(GetFileContentsCmdFlags.Suppress, tempFileName);
            _repository.GetFileContents(printOptions, fs);

            IVsDifferenceService differenceService = _serviceProvider.GetService(typeof(SVsDifferenceService)) as IVsDifferenceService;
            string leftFileMoniker = tempFileName;
            // The difference service will automatically load the text from the file open in the editor, even if
            // it has changed. Don't use the original path here.
            string rightFileMoniker = filename;
            string caption = "p4 diff";

            string leftLabel = string.Format("{0}#head", depotPath);
            string rightLabel = filename;
            string inlineLabel = null;
            string tooltip = null;
            string roles = null;

            __VSDIFFSERVICEOPTIONS grfDiffOptions = __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary;
            differenceService.OpenComparisonWindow2(leftFileMoniker, rightFileMoniker, caption, tooltip, leftLabel, rightLabel, inlineLabel, roles, (uint)grfDiffOptions);

            // Since the file is marked as temporary, we can delete it now
            // Perforce can create read-only file, so set FileAttributes.Normal in order to safe delete it
            System.IO.File.SetAttributes(tempFileName, FileAttributes.Normal);
            System.IO.File.Delete(tempFileName);
        }

        public bool IsGitRepository(string path)
        {
            return _connected && IsFileUnderPerforceRoot(path);
        }

        public string GetConnectionError()
        {
            return _last_error;
        }

        public string GetP4EnvironmentVar(string varName)
        {
            Init();
            string res = null;

            try
            {
                if (_connection != null)
                {
                    res = _connection.GetP4EnvironmentVar(varName);
                }
            }
            catch (P4Exception)
            {
            }
            return res;
        }

        public bool SetP4EnvironmentVar(string varName, string val)
        {
            Init();
            bool res = false;

            try
            {
                if (_connection != null)
                {
                    _connection.SetP4EnvironmentVar(varName, val);
                    res = true;
                }
            }
            catch (P4Exception)
            {
            }
            return res;
        }

        private bool IsFileUnderPerforceRoot(string absolutePath)
        {
            return absolutePath.StartsWith(_perforceRoot, StringComparison.OrdinalIgnoreCase);
        }

        private string GetPerforcePath(string absolutePath)
        {
            Debug.Assert(IsGitRepository(absolutePath));

            string perforcePath = absolutePath.Substring(_perforceRoot.Length, absolutePath.Length - _perforceRoot.Length);
            perforcePath = perforcePath.Replace('\\', '/');
            perforcePath = perforcePath.Insert(0, "/");

            return perforcePath;
        }
    }
}