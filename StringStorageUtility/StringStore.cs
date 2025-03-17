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

        private string _filePath;
        private static readonly object _fileStreamLock = new object();
        private bool _debug;
        private List<string> _stringsList;
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
                CrestronConsole.PrintLine($"Setting _debug to {value == 1}");
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
					AutoSaveIsEnabled?.Invoke(this, new EventArgs()); 
				} 
				else 
				{ 
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
		
		public void Initialize(string path, int  timeoutMs)
		{
			if (_debug) 
			{ 
				CrestronConsole.PrintLine($"Initialize called\n"); 
				CrestronConsole.PrintLine($"FilePath: {path}\n");
			}
            //set file path and timeout. also instantiate some global vars
            _filePath = path;
			TimeoutMs = timeoutMs;
			_stringsList = new List<string>();
			jso = new JsonStringObject();

			ReadFile();

			Initialized = true;

            //raise event for simpl
            IsInitialized?.Invoke(this, new EventArgs());
        }

		public void ReadFile()
		{
			try
			{

                //if we are awaiting a save, we are not awaiting a save anymore. Effectively, this will overwrite pending changes to the file
                _awaitingSave = false;
                NotAwaitingSave?.Invoke(this, new EventArgs());

                //raise event for simpl
                ReadStarted?.Invoke(this, new EventArgs());

                //lock thread for file access
                lock (_fileStreamLock)
                {
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
                                //read file into string, convert string to jso object, instantiate global list of strings
                                var jsonString = File.ReadToEnd(_filePath, Encoding.ASCII);
                                if (jsonString != null)
                                {
                                    jso = JsonConvert.DeserializeObject<JsonStringObject>(jsonString);
                                    _stringsList = new List<string>();

                                    //loop thru strings in jso.Strings
                                    foreach (var item in jso.Strings)
                                    {
                                        //add each string to global stringList var
                                        _stringsList.Add(item.ToString());
                                    }
                                }
                                else
                                {
                                    CrestronConsole.PrintLine($"File Contents empty\n");
                                }
                            }
                            catch (NullReferenceException e)
                            {
                                CrestronConsole.PrintLine($"Error reading string: {e.Message}\n");
                            }
                            catch (JsonReaderException e)
                            {
                                CrestronConsole.PrintLine($"Error reading string: {e.Message}\n");
                            }
                            catch (JsonSerializationException e)
                            {
                                CrestronConsole.PrintLine($"Error deserializing string: {e.Message}\n");
                            }


                        }
                        else //write an empty file
                        {
                            if (_debug) { CrestronConsole.PrintLine($"File not Found. Writing an empty file.\n"); }

                            //instantiate stringList and add a single empty string
                            _stringsList = new List<string>();
                            _stringsList.Add(String.Empty);

                            //write blank file
                            WriteFile();
                        }

                        //send strings to simpl
                        TransportStringsToSimpl(_stringsList);

                        //raise event for simpl
                        ReadComplete?.Invoke(this, new EventArgs());
                    }
                    catch (Exception e)
                    {
                        CrestronConsole.PrintLine($"StringStore: Error in ReadFile(): {e}\n");
                        ErrorLog.Error($"StringStore: Error in ReadFile(): {e}\n");
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
			try
			{
                //lockout other threads from accessing file
                lock (_fileStreamLock)
                {
                    //create filestream
                    FileStream fs = new FileStream(_filePath, FileMode.Create);

					//kill the autosave timer
					if (_saveTimer != null)
					{
						try
						{
							_saveTimer.Stop();
						}
						catch (Exception e)
						{
							CrestronConsole.PrintLine($"Error in WriteFile - _saveTimer {e.Message}");
						}

					}

					//invoke writeStarted event
					WriteStarted?.Invoke(this, new EventArgs());



                    try
                    {
                        //instantiate jso with current info
                        jso = new JsonStringObject
                        {
                            LastUpdated = DateTime.Now.ToString(),
                            StringCount = _stringsList.Count,
                            Strings = _stringsList
                        };

                        //call method to send to simpl
                        TransportStringsToSimpl(jso.Strings);

                        //convert jso to string and write string
                        var jsonString = JsonConvert.SerializeObject(jso);
                        fs.Write(jsonString, Encoding.ASCII);
                        _awaitingSave = false;
                        NotAwaitingSave?.Invoke(this, new EventArgs());
                    }

                    catch (Exception e)
                    {
                        CrestronConsole.PrintLine($"StringStore: Error in WriteFile(): {e}\n");
                        ErrorLog.Error($"StringStore: Error in WriteFile(): {e}\n");
                    }

                    finally
                    {
                        //close stream and raise event for simpl
                        fs.Close();
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

                if (i < 0)
                {
                    return;
                }

				//class must be initialized for the stringList to be defined, otherwise you'd get a "NullReference" exception
				if (Initialized)
				{
					//if the index passed is higher than the list contains, add to end of list
                    if (i >= _stringsList.Count)
                    {
                        _stringsList.Add(s.Trim());
						_awaitingSave = true;

                    }
                    else //otherwise overwrite the value at that index with the passed in string
                    {
                        if (_stringsList[i] != s.Trim())
                        {
                            _stringsList[i] = s.Trim();
                            _awaitingSave = true;
                        }
                    }

					if (_awaitingSave && !_autoSaveEnabled)
					{
                        //raise event for simpl
                        AwaitingSave?.Invoke(this, new EventArgs());
                    }

					//restart a timer that will autosave when it expires
					if (_autoSaveEnabled)
					{
						RestartSaveTimer();
					}
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
			//Create new event args
			StringListUpdateEventArgs args = new StringListUpdateEventArgs();

			//popular members of the eventarg
			args.StringCount = (ushort)_stringsList.Count;
			
			int i = 1;
			foreach (var item in l)
			{
				if (_debug) 
				{ 
					CrestronConsole.PrintLine($"Sending String to Simpl\n");
                    CrestronConsole.PrintLine($"String[{i}]: {item}\n");
                }
				args.StringIndex = (ushort)(i);
				args.StringValue = item;
                StringListUpdated?.Invoke(this, args); //raise event for simpl to ingest eventargs
				i++;
            }
		}

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

		private void SaveTimerCallback(object o)
		{
			//write to file
			WriteFile();
		}

		#endregion
	
    }
}
