using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;

using static Veldrid.Sdl2.Sdl2Native;
using System.ComponentModel;
using Veldrid;

namespace Veldrid.Sdl2
{
    public abstract unsafe class RawSdl2Window
    {
        private readonly List<SDL_Event> _events = new List<SDL_Event>();
        private IntPtr _window;

        // Threaded Sdl2Window flags
        protected readonly bool _threadedProcessing;

        private bool _shouldClose;
        // Cached Sdl2Window state (for threaded processing)
        private BufferedValue<Point> _cachedPosition = new BufferedValue<Point>();
        private BufferedValue<Point> _cachedSize = new BufferedValue<Point>();
        private string _cachedWindowTitle;
        private bool _newWindowTitleReceived;

        public RawSdl2Window(string title, int x, int y, int width, int height, SDL_WindowFlags flags, bool threadedProcessing)
        {
            _threadedProcessing = threadedProcessing;
            if (threadedProcessing)
            {
                using (ManualResetEvent mre = new ManualResetEvent(false))
                {
                    WindowParams wp = new WindowParams()
                    {
                        Title = title,
                        X = x,
                        Y = y,
                        Width = width,
                        Height = height,
                        WindowFlags = flags,
                        ResetEvent = mre
                    };

                    Task.Factory.StartNew(WindowOwnerRoutine, wp, TaskCreationOptions.LongRunning);
                    mre.WaitOne();
                }
            }
            else
            {
                _window = SDL_CreateWindow(title, x, y, width, height, flags);
                WindowID = SDL_GetWindowID(_window);
                Sdl2WindowRegistry.RegisterWindow(this);
                PostWindowCreated(flags);
            }
        }

        public RawSdl2Window(IntPtr windowHandle, bool threadedProcessing)
        {
            _threadedProcessing = threadedProcessing;
            if (threadedProcessing)
            {
                using (ManualResetEvent mre = new ManualResetEvent(false))
                {
                    WindowParams wp = new WindowParams()
                    {
                        WindowHandle = windowHandle,
                        WindowFlags = 0,
                        ResetEvent = mre
                    };

                    Task.Factory.StartNew(WindowOwnerRoutine, wp, TaskCreationOptions.LongRunning);
                    mre.WaitOne();
                }
            }
            else
            {
                _window = SDL_CreateWindowFrom(windowHandle);
                WindowID = SDL_GetWindowID(_window);
                Sdl2WindowRegistry.RegisterWindow(this);
                PostWindowCreated(0);
            }
        }

        public int X { get => _cachedPosition.Value.X; set => SetWindowPosition(value, Y); }
        public int Y { get => _cachedPosition.Value.Y; set => SetWindowPosition(X, value); }

        public int Width { get => GetWindowSize().X; set => SetWindowSize(value, Height); }
        public int Height { get => GetWindowSize().Y; set => SetWindowSize(Width, value); }

        public IntPtr Handle => GetUnderlyingWindowHandle();

        public string Title
        {
            get => _cachedWindowTitle;
            set
            {
                _cachedWindowTitle = value;
                _newWindowTitleReceived = true;
            }
        }

        public WindowState WindowState
        {
            get
            {
                SDL_WindowFlags flags = SDL_GetWindowFlags(_window);
                if (((flags & SDL_WindowFlags.FullScreenDesktop) == SDL_WindowFlags.FullScreenDesktop)
                    || ((flags & (SDL_WindowFlags.Borderless | SDL_WindowFlags.Fullscreen)) == (SDL_WindowFlags.Borderless | SDL_WindowFlags.Fullscreen)))
                {
                    return WindowState.BorderlessFullScreen;
                }
                else if ((flags & SDL_WindowFlags.Minimized) == SDL_WindowFlags.Minimized)
                {
                    return WindowState.Minimized;
                }
                else if ((flags & SDL_WindowFlags.Fullscreen) == SDL_WindowFlags.Fullscreen)
                {
                    return WindowState.FullScreen;
                }
                else if ((flags & SDL_WindowFlags.Maximized) == SDL_WindowFlags.Maximized)
                {
                    return WindowState.Maximized;
                }
                else if ((flags & SDL_WindowFlags.Hidden) == SDL_WindowFlags.Hidden)
                {
                    return WindowState.Hidden;
                }

                return WindowState.Normal;
            }
            set
            {
                switch (value)
                {
                    case WindowState.Normal:
                        SDL_SetWindowFullscreen(_window, SDL_FullscreenMode.Windowed);
                        break;
                    case WindowState.FullScreen:
                        SDL_SetWindowFullscreen(_window, SDL_FullscreenMode.Fullscreen);
                        break;
                    case WindowState.Maximized:
                        SDL_MaximizeWindow(_window);
                        break;
                    case WindowState.Minimized:
                        SDL_MinimizeWindow(_window);
                        break;
                    case WindowState.BorderlessFullScreen:
                        SDL_SetWindowFullscreen(_window, SDL_FullscreenMode.FullScreenDesktop);
                        break;
                    case WindowState.Hidden:
                        SDL_HideWindow(_window);
                        break;
                    default:
                        throw new InvalidOperationException("Illegal WindowState value: " + value);
                }
            }
        }

        public bool Exists { get; private set; }

        public bool Visible
        {
            get => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.Shown) != 0;
            set
            {
                if (value)
                {
                    SDL_ShowWindow(_window);
                }
                else
                {
                    SDL_HideWindow(_window);
                }
            }
        }

        public Vector2 ScaleFactor => Vector2.One;

        public Rectangle Bounds => new Rectangle(_cachedPosition, GetWindowSize());

        public bool CursorVisible
        {
            get
            {
                return SDL_ShowCursor(SDL_QUERY) == 1;
            }
            set
            {
                int toggle = value ? SDL_ENABLE : SDL_DISABLE;
                SDL_ShowCursor(toggle);
            }
        }

        public float Opacity
        {
            get
            {
                float opacity = float.NaN;
                if (SDL_GetWindowOpacity(_window, &opacity) == 0)
                {
                    return opacity;
                }
                return float.NaN;
            }
            set
            {
                SDL_SetWindowOpacity(_window, value);
            }
        }

        public bool Focused => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.InputFocus) != 0;

        public bool Resizable
        {
            get => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.Resizable) != 0;
            set => SDL_SetWindowResizable(_window, value ? 1u : 0u);
        }

        public bool BorderVisible
        {
            get => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.Borderless) == 0;
            set => SDL_SetWindowBordered(_window, value ? 1u : 0u);
        }

        public IntPtr SdlWindowHandle => _window;

        internal uint WindowID { get; private set; }

        public bool LimitPollRate { get; set; }
        public float PollIntervalInMs { get; set; }

        public event Action Resized;
        public event Action Closing;
        public event Action Closed;
        public event Action FocusLost;
        public event Action FocusGained;
        public event Action Shown;
        public event Action Hidden;
        public event Action MouseEntered;
        public event Action MouseLeft;
        public event Action Exposed;
        public event Action<Point> Moved;
        public event Action<DragDropEvent> DragDrop;

        public Point ClientToScreen(Point p)
        {
            Point position = _cachedPosition;
            return new Point(p.X + position.X, p.Y + position.Y);
        }

        public void Close()
        {
            if (_threadedProcessing)
            {
                _shouldClose = true;
            }
            else
            {
                CloseCore();
            }
        }

        private void CloseCore()
        {
            Sdl2WindowRegistry.RemoveWindow(this);
            Closing?.Invoke();
            SDL_DestroyWindow(_window);
            Exists = false;
            Closed?.Invoke();
        }

        private void WindowOwnerRoutine(object state)
        {
            WindowParams wp = (WindowParams)state;
            _window = wp.Create();
            WindowID = SDL_GetWindowID(_window);
            Sdl2WindowRegistry.RegisterWindow(this);
            PostWindowCreated(wp.WindowFlags);
            wp.ResetEvent.Set();

            double previousPollTimeMs = 0;
            Stopwatch sw = new Stopwatch();
            sw.Start();

            while (Exists)
            {
                if (_shouldClose)
                {
                    CloseCore();
                    return;
                }

                double currentTick = sw.ElapsedTicks;
                double currentTimeMs = sw.ElapsedTicks * (1000.0 / Stopwatch.Frequency);
                if (LimitPollRate && currentTimeMs - previousPollTimeMs < PollIntervalInMs)
                {
                    Thread.Sleep(0);
                }
                else
                {
                    previousPollTimeMs = currentTimeMs;
                    ProcessEvents(null);
                }
            }
        }

        private void PostWindowCreated(SDL_WindowFlags flags)
        {
            RefreshCachedPosition();
            RefreshCachedSize();
            if ((flags & SDL_WindowFlags.Shown) == SDL_WindowFlags.Shown)
            {
                SDL_ShowWindow(_window);
            }

            Exists = true;
        }

        // Called by Sdl2EventProcessor when an event for this window is encountered.
        internal void AddEvent(SDL_Event ev)
        {
            _events.Add(ev);
        }

        protected void ProcessEvents(SDLEventHandler eventHandler)
        {
            CheckNewWindowTitle();

            Sdl2Events.ProcessEvents();
            for (int i = 0; i < _events.Count; i++)
            {
                SDL_Event ev = _events[i];
                if (eventHandler == null)
                {
                    HandleEvent(&ev);
                }
                else
                {
                    eventHandler(ref ev);
                }
            }
            _events.Clear();
        }

        public void PumpEvents(SDLEventHandler eventHandler)
        {
            ProcessEvents(eventHandler);
        }

        protected abstract unsafe void OnHandleEvent(SDL_Event* ev);

        private unsafe void HandleEvent(SDL_Event* ev)
        {
            switch (ev->type)
            {
                case SDL_EventType.Quit:
                case SDL_EventType.Terminating:
                    Close();
                    break;

                case SDL_EventType.WindowEvent:
                    SDL_WindowEvent windowEvent = Unsafe.Read<SDL_WindowEvent>(ev);
                    HandleWindowEvent(windowEvent);
                    break;

                case SDL_EventType.DropFile:
                case SDL_EventType.DropBegin:
                case SDL_EventType.DropTest:
                    SDL_DropEvent dropEvent = Unsafe.Read<SDL_DropEvent>(ev);
                    HandleDropEvent(dropEvent);
                    break;

                default:
                    OnHandleEvent(ev);
                    break;
            }
        }

        private void CheckNewWindowTitle()
        {
            if (WindowState != WindowState.Minimized && _newWindowTitleReceived)
            {
                _newWindowTitleReceived = false;
                SDL_SetWindowTitle(_window, _cachedWindowTitle);
            }
        }

        private void HandleDropEvent(SDL_DropEvent dropEvent)
        {
            string file = Utilities.GetString(dropEvent.file);
            SDL_free(dropEvent.file);

            if (dropEvent.type == SDL_EventType.DropFile)
            {
                DragDrop?.Invoke(new DragDropEvent(file));
            }
        }

        private void HandleWindowEvent(SDL_WindowEvent windowEvent)
        {
            switch (windowEvent.@event)
            {
                case SDL_WindowEventID.Resized:
                case SDL_WindowEventID.SizeChanged:
                case SDL_WindowEventID.Minimized:
                case SDL_WindowEventID.Maximized:
                case SDL_WindowEventID.Restored:
                    HandleResizedMessage();
                    break;
                case SDL_WindowEventID.FocusGained:
                    FocusGained?.Invoke();
                    break;
                case SDL_WindowEventID.FocusLost:
                    FocusLost?.Invoke();
                    break;
                case SDL_WindowEventID.Close:
                    Close();
                    break;
                case SDL_WindowEventID.Shown:
                    Shown?.Invoke();
                    break;
                case SDL_WindowEventID.Hidden:
                    Hidden?.Invoke();
                    break;
                case SDL_WindowEventID.Enter:
                    MouseEntered?.Invoke();
                    break;
                case SDL_WindowEventID.Leave:
                    MouseLeft?.Invoke();
                    break;
                case SDL_WindowEventID.Exposed:
                    Exposed?.Invoke();
                    break;
                case SDL_WindowEventID.Moved:
                    _cachedPosition.Value = new Point(windowEvent.data1, windowEvent.data2);
                    Moved?.Invoke(new Point(windowEvent.data1, windowEvent.data2));
                    break;
                default:
                    Debug.WriteLine("Unhandled SDL WindowEvent: " + windowEvent.@event);
                    break;
            }
        }

        private void HandleResizedMessage()
        {
            RefreshCachedSize();
            Resized?.Invoke();
        }

        private void RefreshCachedSize()
        {
            int w, h;
            SDL_GetWindowSize(_window, &w, &h);
            _cachedSize.Value = new Point(w, h);
        }

        private void RefreshCachedPosition()
        {
            int x, y;
            SDL_GetWindowPosition(_window, &x, &y);
            _cachedPosition.Value = new Point(x, y);
        }

        public Point ScreenToClient(Point p)
        {
            Point position = _cachedPosition;
            return new Point(p.X - position.X, p.Y - position.Y);
        }

        private void SetWindowPosition(int x, int y)
        {
            SDL_SetWindowPosition(_window, x, y);
            _cachedPosition.Value = new Point(x, y);
        }

        private Point GetWindowSize()
        {
            return _cachedSize;
        }

        private void SetWindowSize(int width, int height)
        {
            SDL_SetWindowSize(_window, width, height);
            _cachedSize.Value = new Point(width, height);
        }

        private IntPtr GetUnderlyingWindowHandle()
        {
            SDL_SysWMinfo wmInfo;
            SDL_GetVersion(&wmInfo.version);
            SDL_GetWMWindowInfo(_window, &wmInfo);
            if (wmInfo.subsystem == SysWMType.Windows)
            {
                Win32WindowInfo win32Info = Unsafe.Read<Win32WindowInfo>(&wmInfo.info);
                return win32Info.Sdl2Window;
            }

            return _window;
        }

        private class WindowParams
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string Title { get; set; }
            public SDL_WindowFlags WindowFlags { get; set; }

            public IntPtr WindowHandle { get; set; }

            public ManualResetEvent ResetEvent { get; set; }

            public SDL_Window Create()
            {
                if (WindowHandle != IntPtr.Zero)
                {
                    return SDL_CreateWindowFrom(WindowHandle);
                }
                else
                {
                    return SDL_CreateWindow(Title, X, Y, Width, Height, WindowFlags);
                }
            }
        }
    }

    [DebuggerDisplay("{DebuggerDisplayString,nq}")]
    public class BufferedValue<T> where T : struct
    {
        public T Value
        {
            get => Current.Value;
            set
            {
                Back.Value = value;
                Back = Interlocked.Exchange(ref Current, Back);
            }
        }

        private ValueHolder Current = new ValueHolder();
        private ValueHolder Back = new ValueHolder();

        public static implicit operator T(BufferedValue<T> bv) => bv.Value;

        private string DebuggerDisplayString => $"{Current.Value}";

        private class ValueHolder
        {
            public T Value;
        }
    }

    public delegate void SDLEventHandler(ref SDL_Event ev);
}
