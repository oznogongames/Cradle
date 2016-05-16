﻿using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using IStoryThread = System.Collections.Generic.IEnumerable<Cradle.StoryOutput>;

namespace Cradle
{
	public enum StoryState
	{
		Idle = 0,
		Playing = 1,
		Paused = 2,
		Exiting = 3
	}

    public abstract class Story: MonoBehaviour
    {
		public bool AutoPlay = true;
        public string StartPassage = "Start";
		public GameObject[] AdditionalCues;
		public bool OutputStyleTags = true;

		public event Action<StoryPassage> OnPassageEnter;
		public event Action<StoryState> OnStateChanged;
		public event Action<StoryOutput> OnOutput;
		public event Action<StoryOutput> OnOutputRemoved;
		
        public Dictionary<string, StoryPassage> Passages { get; private set; }
		public List<StoryOutput> Output { get; private set; }
		public string[] Tags { get; private set; }
		public RuntimeVars Vars { get; protected set; }
		public string CurrentPassageName { get; private set; }
		public StoryLink CurrentLinkInAction { get; private set; }
		public StoryStyle Style { get; private set; }
		public int NumberOfLinksDone { get; private set; }
        public List<string> PassageHistory {get; private set; }
		public float PassageTime { get { return _timeAccumulated + (Time.time - _timeChangedToPlay); } }

		StoryState _state = StoryState.Idle;
		IEnumerator<StoryOutput> _currentThread = null;
		ThreadResult _lastThreadResult = ThreadResult.Done;
		Cue[] _passageUpdateCues = null;
		string _passageWaitingToEnter = null;
		Dictionary<string, List<Cue>> _cueCache = new Dictionary<string, List<Cue>>();
		MonoBehaviour[] _cueTargets = null;
		float _timeChangedToPlay = 0f;
		float _timeAccumulated;
		List<StyleScope> _scopes = new List<StyleScope>();

		protected Stack<int> InsertStack = new Stack<int>();

		private class Cue
		{
			public MonoBehaviour target;
			public MethodInfo method;
		}

		private enum ThreadResult
		{
			InProgress = 0,
			Done = 1,
			Aborted = 2
		}

		public Story()
		{
			StoryVar.RegisterTypeService<bool>(new BoolService());
			StoryVar.RegisterTypeService<int>(new IntService()); 
			StoryVar.RegisterTypeService<double>(new DoubleService());
			StoryVar.RegisterTypeService<string>(new StringService());

			this.Passages = new Dictionary<string, StoryPassage>();

            this.PassageHistory = new List<string>();
		}

		protected void Init()
		{
			_state = StoryState.Idle;
			this.Output = new List<StoryOutput>();
			this.Tags = new string[0];
			this.Style = new StoryStyle();

			NumberOfLinksDone = 0;
			PassageHistory.Clear();
			InsertStack.Clear();
			
			CurrentPassageName = null;
		}

		void Start()
		{
			if (AutoPlay)
				this.Begin();
		}

		// ---------------------------------
		// State control

		public StoryState State
		{
			get { return _state; }
			private set
			{
				StoryState prev = _state;
				_state = value;
				if (prev != value && OnStateChanged != null)
					OnStateChanged(value);
			}
		}

		public void Reset()
		{
			if (this.State != StoryState.Idle)
				throw new InvalidOperationException("Can only reset a story that is Idle.");

			// Reset twine vars
			if (Vars != null)
				Vars.Reset();

			this.Init();
		}

		/// <summary>
		/// Begins the story by calling GoTo(StartPassage).
		/// </summary>
		public void Begin()
		{
			GoTo(StartPassage);
		}

		/// <summary>
		/// 
		/// </summary>
		public void GoTo(string passageName)
		{
			if (this.State != StoryState.Idle)
			{
				throw new InvalidOperationException(
					// Paused
					this.State == StoryState.Paused ?
						"The story is currently paused. Resume() must be called before advancing to a different passage." :
					// Playing
					this.State == StoryState.Playing || this.State == StoryState.Exiting ?
						"The story can only be advanced when it is in the Idle state." :
					// Complete
						"The story is complete. Reset() must be called before it can be played again."
					);
			}
			
			// Indicate specified passage as next
			_passageWaitingToEnter = passageName;

			if (CurrentPassageName != null)
			{
				this.State = StoryState.Exiting;

				// invoke exit cues
				CuesInvoke(CuesFind("Exit", reverse: true));
			}

			if (this.State != StoryState.Paused)
				Enter(passageName);
		}

		/// <summary>
		/// While the story is playing, pauses the execution of the current story thread.
		/// </summary>
		public void Pause()
		{
			if (this.State != StoryState.Playing && this.State != StoryState.Exiting)
				throw new InvalidOperationException("Pause can only be called while a passage is playing or exiting.");

			this.State = StoryState.Paused;
			_timeAccumulated = Time.time - _timeChangedToPlay;
		}

		/// <summary>
		/// When the story is paused, resumes execution of the current story thread.
		/// </summary>
		public void Resume()
		{
			if (this.State != StoryState.Paused)
			{
				throw new InvalidOperationException(
					// Paused
					this.State == StoryState.Idle ?
						"The story is currently idle. Call Begin, Advance or GoTo to play." :
					// Playing
					this.State == StoryState.Playing || this.State == StoryState.Exiting?
						"Resume() should be called only when the story is paused." :
					// Complete
						"The story is complete. Reset() must be called before it can be played again."
					);
			}
						
			// Either enter the next passage, or Execute if it was already entered
			if (_passageWaitingToEnter != null) {
				Enter(_passageWaitingToEnter);
			}
			else {
				this.State = StoryState.Playing;
				_timeAccumulated = Time.time - _timeChangedToPlay;
				ExecuteCurrentThread();
			}
		}

		StoryPassage GetPassage(string passageName)
		{
			StoryPassage passage;
			if (!Passages.TryGetValue(passageName, out passage))
				throw new CradleException(String.Format("Passage '{0}' does not exist.", passageName));
			return passage;
		}

		void Enter(string passageName)
		{
			_passageWaitingToEnter = null;
			_timeAccumulated = 0;
			_timeChangedToPlay = Time.time;

			this.InsertStack.Clear();
			this.Output.Clear();
			this.Style = new StoryStyle();
			_passageUpdateCues = null;

			StoryPassage passage = GetPassage(passageName);
			this.Tags = (string[])passage.Tags.Clone();
			this.CurrentPassageName = passage.Name;

            PassageHistory.Add(passageName);

			// Invoke the general passage enter event
			if (this.OnPassageEnter != null)
				this.OnPassageEnter(passage);

			// Add output (and trigger cues)
			OutputAdd(passage);

			// Get update cues for calling during update
			_passageUpdateCues = CuesFind("Update", reverse: false, allowCoroutines: false).ToArray();

			// Prepare the thread enumerator
			_currentThread = CollapseThread(passage.GetMainThread()).GetEnumerator();
			CurrentLinkInAction = null;

			this.State = StoryState.Playing;
			OutputSend(passage);
			CuesInvoke(CuesFind("Enter", maxLevels: 1));

			// Story was paused, wait for it to resume
			if (this.State == StoryState.Paused)
				return;
			else
				ExecuteCurrentThread();
		}

		/// <summary>
		/// Executes the current thread by advancing its enumerator, sending its output and invoking cues.
		/// </summary>
		void ExecuteCurrentThread()
		{
			Abort aborted = null;

			while (_currentThread.MoveNext())
			{
				StoryOutput output = _currentThread.Current;

				// Abort this thread
				if (output is Abort)
				{
					aborted = (Abort) output;
					break;
				}

				OutputAdd(output);

				// Let the handlers and cues kick in
				if (output is StoryPassage)
				{
					CuesInvoke(CuesFind("Enter", reverse: true, maxLevels: 1));

					// Refresh the update cues
					_passageUpdateCues = CuesFind("Update", reverse: false, allowCoroutines: false).ToArray();
				}

				// Send output
				OutputSend(output);
				CuesInvoke(CuesFind("Output"), output);

				// Story was paused, wait for it to resume
				if (this.State == StoryState.Paused)
				{
					_lastThreadResult = ThreadResult.InProgress;
					return;
				}
			}

			_currentThread.Dispose();
			_currentThread = null;

			this.State = StoryState.Idle;

			// Return the appropriate result
			if (aborted != null)
			{
				CuesInvoke(CuesFind("Aborted"));
				if (aborted.GoToPassage != null)
					this.GoTo(aborted.GoToPassage);

				_lastThreadResult = ThreadResult.Aborted;
			}
			else
			{
				// Invoke the done cue - either for main or for a link
				if (CurrentLinkInAction == null)
					CuesInvoke(CuesFind("Done"));
				else
					CuesInvoke(CuesFind("ActionDone"), CurrentLinkInAction);

				_lastThreadResult = ThreadResult.Done;
			}

			CurrentLinkInAction = null;
		}

		/// <summary>
		/// Invokes and bubbles up output of embedded fragments and passages.
		/// </summary>
		IStoryThread CollapseThread(IStoryThread thread)
		{
			foreach (StoryOutput output in thread)
			{
				//foreach (TwineOutput scopeTag in ScopeOutputTags())
				//	yield return scopeTag;

				if (output is Embed)
				{
					var embed = (Embed) output;
					IStoryThread embeddedThread;
					if (embed is EmbedPassage)
					{
						var embedInfo = (EmbedPassage)embed;
						StoryPassage passage = GetPassage(embedInfo.Name);
						embeddedThread = passage.GetMainThread();
					}
					else if (embed is EmbedFragment)
					{
						var embedInfo = (EmbedFragment)embed;
                        embeddedThread = embedInfo.GetThread();
					}
					else
						continue;

					// First yield the embed
					yield return embed;

					// Output the content
					foreach (StoryOutput innerOutput in CollapseThread(embeddedThread))
					{
						innerOutput.EmbedInfo = embed;
						yield return innerOutput;
					}
				}
				else
					yield return output;
			}

			//foreach (TwineOutput scopeTag in ScopeOutputTags())
			//	yield return scopeTag;
		}

		void OutputAdd(StoryOutput output)
		{
			// Insert the output into the right place
			int insertIndex = InsertStack.Count > 0 ? InsertStack.Peek() : -1;

			if (insertIndex < 0)
			{
				output.Index = this.Output.Count;
				this.Output.Add(output);
			}
			else
			{
				// When a valid insert index is specified, update the following outputs' index
				output.Index = insertIndex;
				this.Output.Insert(insertIndex, output);
				OutputUpdateIndexes(insertIndex + 1);
			}

			// Increase the topmost index
			if (InsertStack.Count > 0 && insertIndex >= 0)
				InsertStack.Push(InsertStack.Pop() + 1);
		}

		void OutputSend(StoryOutput output, bool add = false)
		{
			output.Style = this.Style;

			if (add)
				OutputAdd(output);

			if (OnOutput != null)
				OnOutput(output);
		}

		protected void OutputRemove(StoryOutput output)
		{
			if (this.Output.Remove(output))
			{
				if (OnOutputRemoved != null)
					OnOutputRemoved(output);
				OutputUpdateIndexes(output.Index);
			}
		}

		void OutputUpdateIndexes(int startIndex)
		{
			for (int i = startIndex; i < this.Output.Count; i++)
				this.Output[i].Index = i;
		}


		public IEnumerable<StoryLink> GetCurrentLinks()
		{
			return this.Output.Where(o => o is StoryLink).Cast<StoryLink>();
		}

		public IEnumerable<StoryText> GetCurrentText()
		{
			return this.Output.Where(o => o is StoryText).Cast<StoryText>();
		}

		// ---------------------------------
		// Scope control

		protected StyleScope ApplyStyle(string setting, object value)
		{
			return ApplyStyle(new StoryStyle(setting, value));
		}

		protected StyleScope ApplyStyle(StoryStyle style)
		{
			return ScopeOpen(style);
		}

		/// <summary>
		/// Helper method to create a new style scope.
		/// </summary>
		StyleScope ScopeOpen(StoryStyle style)
		{
			StyleScope scope = new StyleScope()
			{
				Style = style
			};
			scope.OnDisposed += ScopeClose;

			_scopes.Add(scope);
			ScopeBuildStyle();

			if (OutputStyleTags)
				OutputSend(new StyleTag(StyleTagType.Opener, scope.Style), add: true);

			return scope;
		}

		void ScopeClose(StyleScope scope)
		{
			scope.OnDisposed -= ScopeClose;

			if (OutputStyleTags)
				OutputSend(new StyleTag(StyleTagType.Closer, scope.Style), add: true);

			_scopes.Remove(scope);
			ScopeBuildStyle();
		}

		void ScopeBuildStyle()
		{
			StoryStyle style = new StoryStyle();
			for (int i = 0; i < _scopes.Count; i++)
				style += _scopes[i].Style;

			this.Style = style;
		}
		
		// ---------------------------------
		// Links

		public void DoLink(StoryLink link)
		{
			if (this.State != StoryState.Idle)
			{
				throw new InvalidOperationException(
					// Paused
					this.State == StoryState.Paused ?
						"The story is currently paused. Resume() must be called before a link can be used." :
					// Playing
					this.State == StoryState.Playing || this.State == StoryState.Exiting ?
						"A link can be used only when the story is in the Idle state." :
					// Complete
						"The story is complete. Reset() must be called before it can be played again."
					);
			}

			// Process the link action before continuing
			if (link.Action != null)
			{
				CurrentLinkInAction = link;

				// Action might invoke a fragment method, in which case we need to process it with cues etc.
				IStoryThread linkActionThread = link.Action.Invoke();
				if (linkActionThread != null)
				{
					// Prepare the fragment thread enumerator
					_currentThread = CollapseThread(linkActionThread).GetEnumerator();

					// Resume story, this time with the actoin thread
					this.State = StoryState.Playing;

					ExecuteCurrentThread();
				}
			}

			// Continue to the link passage only if a fragment thread (opened by the action) isn't in progress
			if (link.PassageName != null && _lastThreadResult == ThreadResult.Done)
			{
				NumberOfLinksDone++;
				GoTo(link.PassageName);
			}
		}

		public void DoLink(int linkIndex)
		{
			DoLink(this.GetCurrentLinks().ElementAt(linkIndex));
		}

		public void DoLink(string linkName)
		{
			StoryLink link = GetLink(linkName, true);
			DoLink(link);
		}

		public bool HasLink(string linkName)
		{
			return GetLink(linkName) != null;
		}

		public StoryLink GetLink(string linkName, bool throwException = false)
		{
			StoryLink link = this.GetCurrentLinks()
				.Where(lnk => string.Equals(lnk.Name, linkName, System.StringComparison.OrdinalIgnoreCase))
				.FirstOrDefault();

			if (link == null && throwException)
				throw new CradleException(string.Format("There is no available link with the name '{0}'.", linkName));

			return link;
		}

		[Obsolete("Use DoLink instead.")]
		public void Advance(StoryLink link)
		{
			DoLink(link);
		}

		[Obsolete("Use DoLink instead.")]
		public void Advance(int linkIndex)
		{
			DoLink(linkIndex);
		}

		[Obsolete("Use DoLink instead.")]
		public void Advance(string linkName)
		{
			DoLink(linkName);
		}

		// ---------------------------------
		// Cues

		static Regex _validPassageNameRegex = new Regex("^[a-z_][a-z0-9_]*$", RegexOptions.IgnoreCase);
		const BindingFlags _cueMethodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

		void Update()
		{
			CuesInvoke(_passageUpdateCues);
		}

		void CuesInvoke(IEnumerable<Cue> cues, params object[] args)
		{
			if (cues == null)
				return;

			if (cues is Cue[])
			{
				var ar = (Cue[]) cues;
				for (int i = 0; i < ar.Length; i++)
					CueInvoke(ar[i], args);
			}
			else
			{
				foreach (Cue cue in cues)
					CueInvoke(cue, args);
			}
		}

		IEnumerable<Cue> CuesFind(string cueName, int maxLevels = 0, bool reverse = false, bool allowCoroutines = true)
		{
			int c = 0;
			for(
				int i = reverse ? this.Output.Count - 1 : 0;
				reverse ? i >= 0 : i < this.Output.Count;
				c++, i = i + (reverse ? -1 : 1)
				)
			{
				if (!(this.Output[i] is StoryPassage))
					continue;

				var passage = (StoryPassage)this.Output[i];

				List<Cue> cues = CueGetMethods(passage.Name, cueName, allowCoroutines);
				if (cues != null)
				{
					for (int h = 0; h < cues.Count; h++)
						yield return cues[h];
					
					if (maxLevels > 0 && c == maxLevels-1)
						yield break;
				}
			}
		}

		void CueInvoke(Cue cue, object[] args)
		{
			object result = null;
			try { result = cue.method.Invoke(cue.target, args); }
			catch(TargetParameterCountException)
			{
				Debug.LogWarningFormat("The cue {0} doesn't have the right parameters so it is being ignored.",
					cue.method.Name
				);
				return;
			}

			if (result is IEnumerator)
				StartCoroutine(((IEnumerator)result));
		}

		MonoBehaviour[] CueGetTargets()
		{
			if (_cueTargets == null)
			{
				// ...................
				// Get all hook targets
				GameObject[] cueTargets;
				if (this.AdditionalCues != null)
				{
					cueTargets = new GameObject[this.AdditionalCues.Length + 1];
					cueTargets[0] = this.gameObject;
					this.AdditionalCues.CopyTo(cueTargets, 1);
				}
				else
					cueTargets = new GameObject[] { this.gameObject };

				// Get all types
				var sources = new List<MonoBehaviour>();
				for (int i = 0; i < cueTargets.Length; i++)
					sources.AddRange(cueTargets[i].GetComponents<MonoBehaviour>());

				_cueTargets = sources.ToArray();
			}

			return _cueTargets;
		}

		List<Cue> CueGetMethods(string passageName, string cueName, bool allowCoroutines = true)
		{
			string methodName = passageName + "_" + cueName;

			List<Cue> cues = null;
			if (!_cueCache.TryGetValue(methodName, out cues))
			{
				MonoBehaviour[] targets = CueGetTargets();
				for (int i = 0; i < targets.Length; i++)
				{
					Type targetType = targets[i].GetType();

					// First try to get a method with an attribute
					MethodInfo method = targetType.GetMethods(_cueMethodFlags)
						.Where(m => m.GetCustomAttributes(typeof(StoryCueAttribute), true)
							.Cast<StoryCueAttribute>()
							.Where(attr => attr.PassageName == passageName && attr.CueName == cueName)
							.Count() > 0
						)
						.FirstOrDefault();
					
					// If failed, try to get the method by name (if valid)
					if (method == null && _validPassageNameRegex.IsMatch(passageName))
						method = targetType.GetMethod(methodName, _cueMethodFlags);

					// No method found on this source type
					if (method == null)
						continue;
					
					// Validate the found method
					if (allowCoroutines)
					{
						if (method.ReturnType != typeof(void) && !typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
						{
							Debug.LogError(targetType.Name + "." + methodName + " must return void or IEnumerator in order to be used as a cue.");
							method = null;
						}
					}
					else
					{
						if (method.ReturnType != typeof(void))
						{
							Debug.LogError(targetType.Name + "." + methodName + " must return void in order to be used as a cue.");
							method = null;
						}
					}

					// The found method wasn't valid
					if (method == null)
						continue;

					// Init the method list
					if (cues == null)
						cues = new List<Cue>();

					cues.Add(new Cue() { method = method, target = targets[i] } );
				}

				// Cache the method list even if it's null so we don't do another lookup next time around (lazy load)
				_cueCache.Add(methodName, cues);
			}

			return cues;
		}

		public void CuesClear()
		{
			_cueCache.Clear();
			_cueTargets = null;
		}


		// ---------------------------------
		// Shorthand functions

		protected StoryVar v(string val)
		{
			return new StoryVar(val);
		}

		protected StoryVar v(double val)
		{
			return new StoryVar(val);
		}

		protected StoryVar v(int val)
		{
			return new StoryVar(val);
		}

		protected StoryVar v(bool val)
		{
			return new StoryVar(val);
		}

		protected StoryVar v(object val)
		{
			return new StoryVar(val);
		}

		protected StoryText text(StoryVar text)
		{
			return new StoryText(StoryVar.ConvertTo<string>(text.Value, strict: false));
		}

		protected LineBreak lineBreak()
		{
			return new LineBreak();
		}

		protected StoryLink link(string text, string passageName, Func<IStoryThread> action)
		{
			return new StoryLink(text, passageName, action);
		}

		protected Abort abort(string goToPassage)
		{
			return new Abort(goToPassage);
		}

		protected EmbedFragment fragment(Func<IStoryThread> action)
		{
			return new EmbedFragment(action);
		}

		protected EmbedPassage passage(string passageName, params StoryVar[] parameters)
		{
			return new EmbedPassage(passageName, parameters);
		}

		protected StoryStyle style(string setting, object value)
		{
			return new StoryStyle(setting, value);
		}

		protected StoryStyle style(StoryVar expression)
		{
			return new StoryStyle(expression);
		}
	}
}