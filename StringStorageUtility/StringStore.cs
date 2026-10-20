using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp.CrestronIO;
using Crestron.SimplSharp;
using Newtonsoft.Json;


namespace StringStorageUtility
{
    public class StringStore
    {
        #region Variables

        private const int MaxStrings = 100;

        private string _filePath;
        private readonly object _fileStreamLock = new object(); //serializes file access
        private readonly object _listLock = new object();       //guards _stringsList, _awaitingSave and the save timer
        private bool _debug;
        private List<string> _stringsList = new List<string>();
        private CTimer _saveTimer;
        private int _timeoutMs;
        private JsonStringObject jso;
        private bool Initialized = false;
        private bool _autoSaveEnabled;
        private bool _awaitingSave = false;

        #endregion

        #region	Proprties
        public ushort Debug
        {
            get
            {
                return (ushort)(_debug ? 1 : 0);
            }
            set
            {
                _debug = value == 1;
            }
        }

        public string FilePath
        {
            get { return _filePath; }
            set { _filePath = value; }
        }

        public int TimeoutMs
        {
            get { return _timeoutMs; }
            set { _timeoutMs = value; }
        }

        public ushort AutoSaveEnabled
        {
            get
            {
                return ((ushort)(_autoSaveEnabled ? 1 : 0));
            }

            set
            {
                _autoSaveEnabled = value == 1;

                if (_autoSaveEnabled)
                {
                    //pick up edits that were made while autosave was off
                    lock (_listLock)
                    {
                        if (_awaitingSave && Initialized)
                        {
                            RestartSaveTimer();
                        }
                    }

                    AutoSaveIsEnabled?.Invoke(this, new EventArgs());
                }
                else
                {
                    //a pending autosave no longer applies; a manual write is needed now
                    StopSaveTimer();

                    AutoSaveIsDisabled?.Invoke(this, new EventArgs());
                }
            }
        }

        #endregion

        #region Events
        public event EventHandler<StringListUpdateEventArgs> StringListUpdated;
        public event EventHandler<EventArgs> FileFound;
        public event EventHandler<EventArgs> IsInitialized;
        public event EventHandler<EventArgs> ReadStarted;
        public event EventHandler<EventArgs> WriteStarted;
        public event EventHandler<EventArgs> ReadComplete;
        public event EventHandler<EventArgs> WriteComplete;
        public event EventHandler<EventArgs> AutoSaveIsEnabled;
        public event EventHandler<EventArgs> AutoSaveIsDisabled;
        public event EventHandler<EventArgs> AwaitingSave;
        public event EventHandler<EventArgs> NotAwaitingSave;
        #endregion

        #region Methods

        public StringStore()
        {
            //empty constructor
        }

        public void Initialize(string path, int timeoutMs)
        {
            if (_debug)
            {
                CrestronConsole.PrintLine($"Initialize called\n");
                CrestronConsole.PrintLine($"FilePath: {path}\n");
            }

            //set file path and timeout. also reset state in case Initialize is called again
            StopSaveTimer();
            _filePath = path;
            TimeoutMs = timeoutMs;
            lock (_listLock)
            {
                _stringsList = new List<string>();
            }
            jso = new JsonStringObject();

            ReadFile();

            Initialized = true;

            //raise event for simpl
            IsInitialized?.Invoke(this, new EventArgs());
        }

        public void ReadFile()
        {
            if (String.IsNullOrEmpty(_filePath))
            {
                CrestronConsole.PrintLine($"StringStore: ReadFile called with no FilePath\n");
                return;
            }

            try
            {
                //lock thread for file access
                lock (_fileStreamLock)
                {
                    //a read replaces whatever is in memory, so pending edits and the pending save are dropped
                    StopSaveTimer();
                    lock (_listLock)
                    {
                        _awaitingSave = false;
                    }
                    NotAwaitingSave?.Invoke(this, new EventArgs());

                    //raise event for simpl
                    ReadStarted?.Invoke(this, new EventArgs());

                    try
                    {
                        //check if file exists
                        if (File.Exists(_filePath))
                        {
                            if (_debug) { CrestronConsole.PrintLine($"File Found. Reading Contents.\n"); }

                            //raise event for simpl
                            FileFound?.Invoke(this, new EventArgs());

                            try
                            {
                                //read file into string, convert string to jso object, rebuild the list of strings
                                var jsonString = File.ReadToEnd(_filePath, Encoding.UTF8);
                                var parsed = String.IsNullOrEmpty(jsonString) ? null : JsonConvert.DeserializeObject<JsonStringObject>(jsonString);

                                if (parsed != null && parsed.Strings != null)
                                {
                                    jso = parsed;

                                    var newList = new List<string>();
                                    foreach (var item in parsed.Strings)
                                    {
                                        if (newList.Count >= MaxStrings) { break; }
                                        newList.Add(item ?? String.Empty);
                                    }

                                    lock (_listLock)
                                    {
                                        _stringsList = newList;
                                    }
                                }
                                else
                                {
                                    CrestronConsole.PrintLine($"File contents empty or invalid\n");
                                }
                            }
                            catch (Exception e)
                            {
                                CrestronConsole.PrintLine($"Error reading strings from file: {e.Message}\n");
                            }
                        }
                        else //write an empty file
                        {
                            if (_debug) { CrestronConsole.PrintLine($"File not Found. Writing an empty file.\n"); }

                            //instantiate stringList and add a single empty string
                            lock (_listLock)
                            {
                                _stringsList = new List<string>();
                                _stringsList.Add(String.Empty);
                            }

                            //write blank file
                            WriteFile();
                        }

                        //send strings to simpl
                        List<string> snapshot;
                        lock (_listLock)
                        {
                            snapshot = new List<string>(_stringsList);
                        }
                        TransportStringsToSimpl(snapshot);
                    }
                    catch (Exception e)
                    {
                        CrestronConsole.PrintLine($"StringStore: Error in ReadFile(): {e}\n");
                        ErrorLog.Error($"StringStore: Error in ReadFile(): {e}\n");
                    }
                    finally
                    {
                        //always raise so the Reading output cannot get stuck high
                        ReadComplete?.Invoke(this, new EventArgs());
                    }
                }
            }
            catch (Exception e)
            {
                CrestronConsole.PrintLine($"Errror in ReadFile - Main: {e.Message}\n{e.StackTrace}");
            }
        }

        public void WriteFile()
        {
            if (String.IsNullOrEmpty(_filePath))
            {
                CrestronConsole.PrintLine($"StringStore: WriteFile called with no FilePath\n");
                return;
            }

            try
            {
                //lockout other threads from accessing file
                lock (_fileStreamLock)
                {
                    //kill the autosave timer
                    StopSaveTimer();

                    //invoke writeStarted event
                    WriteStarted?.Invoke(this, new EventArgs());

                    //take a copy so edits arriving mid-write can't break serialization; anything newer re-flags _awaitingSave
                    List<string> snapshot;
                    lock (_listLock)
                    {
                        snapshot = new List<string>(_stringsList);
                        _awaitingSave = false;
                    }

                    try
                    {
                        //instantiate jso with current info
                        jso = new JsonStringObject
                        {
                            LastUpdated = DateTime.Now.ToString(),
                            StringCount = snapshot.Count,
                            Strings = snapshot
                        };

                        //serialize first so a failure here can't leave a truncated file
                        var jsonString = JsonConvert.SerializeObject(jso);

                        FileStream fs = new FileStream(_filePath, FileMode.Create);
                        try
                        {
                            fs.Write(jsonString, Encoding.UTF8);
                        }
                        finally
                        {
                            fs.Close();
                        }

                        //only report "saved" if nothing was edited while writing
                        bool stillPending;
                        lock (_listLock)
                        {
                            stillPending = _awaitingSave;
                        }
                        if (!stillPending)
                        {
                            NotAwaitingSave?.Invoke(this, new EventArgs());
                        }
                    }
                    catch (Exception e)
                    {
                        CrestronConsole.PrintLine($"StringStore: Error in WriteFile(): {e}\n");
                        ErrorLog.Error($"StringStore: Error in WriteFile(): {e}\n");

                        //the data is not on disk
                        lock (_listLock)
                        {
                            _awaitingSave = true;
                        }
                        AwaitingSave?.Invoke(this, new EventArgs());
                    }
                    finally
                    {
                        WriteComplete?.Invoke(this, new EventArgs());
                    }
                }
            }
            catch (Exception e)
            {
                CrestronConsole.PrintLine($"Error in Writefile Main: {e.Message}");
            }
        }

        public void SetStringFromSimpl(ushort i, string s)
        {
            try
            {
                if (_debug)
                {
                    CrestronConsole.PrintLine($"Recieved string from simpl\n");
                    CrestronConsole.PrintLine($"string[{i}]: {s}\n");
                }

                //class must be initialized, and the index must fit the simpl arrays
                if (!Initialized || i >= MaxStrings)
                {
                    return;
                }

                string value = (s ?? String.Empty).Trim();
                bool newlyAwaiting = false;

                lock (_listLock)
                {
                    //pad with empty strings so index i lands in slot i
                    while (_stringsList.Count <= i)
                    {
                        _stringsList.Add(String.Empty);
                    }

                    if (_stringsList[i] != value)
                    {
                        _stringsList[i] = value;

                        if (!_awaitingSave)
                        {
                            _awaitingSave = true;
                            newlyAwaiting = true;
                        }
                    }

                    //restart a timer that will autosave when it expires
                    if (_autoSaveEnabled && _awaitingSave)
                    {
                        RestartSaveTimer();
                    }
                }

                //raise event for simpl: an edit exists that is not in the file yet
                if (newlyAwaiting)
                {
                    AwaitingSave?.Invoke(this, new EventArgs());
                }
            }
            catch (Exception e)
            {
                CrestronConsole.PrintLine($"StringStore: Error in SetStringFromSimpl(): {e}\n");
                ErrorLog.Error($"StringStore: Error in SetStringFromSimpl(): {e}\n");
            }
        }

        private void TransportStringsToSimpl(List<string> l)
        {
            //send every slot so outputs left over from a longer list are cleared
            for (int i = 0; i < MaxStrings; i++)
            {
                string item = i < l.Count ? l[i] : String.Empty;

                if (_debug)
                {
                    CrestronConsole.PrintLine($"Sending String to Simpl\n");
                    CrestronConsole.PrintLine($"String[{i + 1}]: {item}\n");
                }

                StringListUpdateEventArgs args = new StringListUpdateEventArgs
                {
                    StringCount = (ushort)l.Count,
                    StringIndex = (ushort)(i + 1),
                    StringValue = item
                };
                StringListUpdated?.Invoke(this, args); //raise event for simpl to ingest eventargs
            }
        }

        //caller must hold _listLock
        private void RestartSaveTimer()
        {
            //if a timer is running, kill it
            if (_saveTimer != null)
            {
                _saveTimer.Stop();
                _saveTimer.Dispose();
            }

            //start a new timer that will call SaveTimerCallback when it expires
            _saveTimer = new CTimer(SaveTimerCallback, null, _timeoutMs, Timeout.Infinite);
        }

        private void StopSaveTimer()
        {
            lock (_listLock)
            {
                if (_saveTimer != null)
                {
                    try
                    {
                        _saveTimer.Stop();
                        _saveTimer.Dispose();
                    }
                    catch (Exception e)
                    {
                        CrestronConsole.PrintLine($"Error stopping save timer: {e.Message}");
                    }
                    _saveTimer = null;
                }
            }
        }

        private void SaveTimerCallback(object o)
        {
            //write to file
            WriteFile();
        }

        #endregion

    }
}
