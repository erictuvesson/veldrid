using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using static Veldrid.Sdl2.Sdl2Native;

namespace Veldrid.Sdl2
{
    public unsafe class Sdl2Window : RawSdl2Window
    {
        private SimpleInputSnapshot _publicSnapshot = new SimpleInputSnapshot();
        private SimpleInputSnapshot _privateSnapshot = new SimpleInputSnapshot();
        private SimpleInputSnapshot _privateBackbuffer = new SimpleInputSnapshot();

        // Current input states
        private int _currentMouseX;
        private int _currentMouseY;
        private bool[] _currentMouseButtonStates = new bool[13];
        private Vector2 _currentMouseDelta;

        private bool _firstMouseEvent = true;

        public Sdl2Window(string title, int x, int y, int width, int height, SDL_WindowFlags flags, bool threadedProcessing)
            : base(title, x, y, width, height, flags, threadedProcessing)
        {
        }

        public Sdl2Window(IntPtr windowHandle, bool threadedProcessing)
            : base(windowHandle, threadedProcessing)
        {
        }

        public Vector2 MouseDelta => _currentMouseDelta;

        public event Action<MouseWheelEventArgs> MouseWheel;
        public event Action<MouseMoveEventArgs> MouseMove;
        public event Action<MouseEvent> MouseDown;
        public event Action<MouseEvent> MouseUp;
        public event Action<KeyEvent> KeyDown;
        public event Action<KeyEvent> KeyUp;

        public void SetMousePosition(Vector2 position) => SetMousePosition((int)position.X, (int)position.Y);
        public void SetMousePosition(int x, int y)
        {
            if (Exists)
            {
                SDL_WarpMouseInWindow(SdlWindowHandle, x, y);
                _currentMouseX = x;
                _currentMouseY = y;
            }
        }

        public InputSnapshot PumpEvents()
        {
            _currentMouseDelta = new Vector2();
            if (_threadedProcessing)
            {
                SimpleInputSnapshot snapshot = Interlocked.Exchange(ref _privateSnapshot, _privateBackbuffer);
                snapshot.CopyTo(_publicSnapshot);
                snapshot.Clear();
            }
            else
            {
                ProcessEvents(null);
                _privateSnapshot.CopyTo(_publicSnapshot);
                _privateSnapshot.Clear();
            }

            return _publicSnapshot;
        }

        protected override unsafe void OnHandleEvent(SDL_Event* ev)
        {
            switch (ev->type)
            {
                /* Keyboard Events */
                case SDL_EventType.KeyDown:
                case SDL_EventType.KeyUp:
                    SDL_KeyboardEvent keyboardEvent = Unsafe.Read<SDL_KeyboardEvent>(ev);
                    HandleKeyboardEvent(keyboardEvent);
                    break;
                case SDL_EventType.TextEditing:
                    break;
                case SDL_EventType.TextInput:
                    SDL_TextInputEvent textInputEvent = Unsafe.Read<SDL_TextInputEvent>(ev);
                    HandleTextInputEvent(textInputEvent);
                    break;
                case SDL_EventType.KeyMapChanged:
                    break;

                /* Mouse Events */
                case SDL_EventType.MouseMotion:
                    SDL_MouseMotionEvent mouseMotionEvent = Unsafe.Read<SDL_MouseMotionEvent>(ev);
                    HandleMouseMotionEvent(mouseMotionEvent);
                    break;
                case SDL_EventType.MouseButtonDown:
                case SDL_EventType.MouseButtonUp:
                    SDL_MouseButtonEvent mouseButtonEvent = Unsafe.Read<SDL_MouseButtonEvent>(ev);
                    HandleMouseButtonEvent(mouseButtonEvent);
                    break;
                case SDL_EventType.MouseWheel:
                    SDL_MouseWheelEvent mouseWheelEvent = Unsafe.Read<SDL_MouseWheelEvent>(ev);
                    HandleMouseWheelEvent(mouseWheelEvent);
                    break;
            }
        }

        private void HandleTextInputEvent(SDL_TextInputEvent textInputEvent)
        {
            uint byteCount = 0;
            // Loop until the null terminator is found or the max size is reached.
            while (byteCount < SDL_TextInputEvent.MaxTextSize && textInputEvent.text[byteCount++] != 0)
            { }

            if (byteCount > 1)
            {
                // We don't want the null terminator.
                byteCount -= 1;
                int charCount = Encoding.UTF8.GetCharCount(textInputEvent.text, (int)byteCount);
                char* charsPtr = stackalloc char[charCount];
                Encoding.UTF8.GetChars(textInputEvent.text, (int)byteCount, charsPtr, charCount);
                for (int i = 0; i < charCount; i++)
                {
                    _privateSnapshot.KeyCharPressesList.Add(charsPtr[i]);
                }
            }
        }

        private void HandleMouseWheelEvent(SDL_MouseWheelEvent mouseWheelEvent)
        {
            _privateSnapshot.WheelDelta += mouseWheelEvent.y;
            MouseWheel?.Invoke(new MouseWheelEventArgs(GetCurrentMouseState(), (float)mouseWheelEvent.y));
        }

        private void HandleMouseButtonEvent(SDL_MouseButtonEvent mouseButtonEvent)
        {
            MouseButton button = KeyMapper.MapMouseButton(mouseButtonEvent.button);
            bool down = mouseButtonEvent.state == 1;
            _currentMouseButtonStates[(int)button] = down;
            _privateSnapshot.MouseDown[(int)button] = down;
            MouseEvent mouseEvent = new MouseEvent(button, down);
            _privateSnapshot.MouseEventsList.Add(mouseEvent);
            if (down)
            {
                MouseDown?.Invoke(mouseEvent);
            }
            else
            {
                MouseUp?.Invoke(mouseEvent);
            }
        }

        private void HandleMouseMotionEvent(SDL_MouseMotionEvent mouseMotionEvent)
        {
            Vector2 mousePos = new Vector2(mouseMotionEvent.x, mouseMotionEvent.y);
            Vector2 delta = new Vector2(mouseMotionEvent.xrel, mouseMotionEvent.yrel);
            _currentMouseX = (int)mousePos.X;
            _currentMouseY = (int)mousePos.Y;
            _privateSnapshot.MousePosition = mousePos;

            if (!_firstMouseEvent)
            {
                _currentMouseDelta += delta;
                MouseMove?.Invoke(new MouseMoveEventArgs(GetCurrentMouseState(), mousePos));
            }

            _firstMouseEvent = false;
        }

        private void HandleKeyboardEvent(SDL_KeyboardEvent keyboardEvent)
        {
            SimpleInputSnapshot snapshot = _privateSnapshot;
            KeyEvent keyEvent = new KeyEvent(KeyMapper.MapKey(keyboardEvent.keysym), keyboardEvent.state == 1, KeyMapper.MapModifierKeys(keyboardEvent.keysym.mod));
            snapshot.KeyEventsList.Add(keyEvent);
            if (keyboardEvent.state == 1)
            {
                KeyDown?.Invoke(keyEvent);
            }
            else
            {
                KeyUp?.Invoke(keyEvent);
            }
        }

        private MouseState GetCurrentMouseState()
        {
            return new MouseState(
                _currentMouseX, _currentMouseY,
                _currentMouseButtonStates[0], _currentMouseButtonStates[1],
                _currentMouseButtonStates[2], _currentMouseButtonStates[3],
                _currentMouseButtonStates[4], _currentMouseButtonStates[5],
                _currentMouseButtonStates[6], _currentMouseButtonStates[7],
                _currentMouseButtonStates[8], _currentMouseButtonStates[9],
                _currentMouseButtonStates[10], _currentMouseButtonStates[11],
                _currentMouseButtonStates[12]);
        }

        private class SimpleInputSnapshot : InputSnapshot
        {
            public List<KeyEvent> KeyEventsList { get; private set; } = new List<KeyEvent>();
            public List<MouseEvent> MouseEventsList { get; private set; } = new List<MouseEvent>();
            public List<char> KeyCharPressesList { get; private set; } = new List<char>();

            public IReadOnlyList<KeyEvent> KeyEvents => KeyEventsList;

            public IReadOnlyList<MouseEvent> MouseEvents => MouseEventsList;

            public IReadOnlyList<char> KeyCharPresses => KeyCharPressesList;

            public Vector2 MousePosition { get; set; }

            private bool[] _mouseDown = new bool[13];
            public bool[] MouseDown => _mouseDown;
            public float WheelDelta { get; set; }

            public bool IsMouseDown(MouseButton button)
            {
                return _mouseDown[(int)button];
            }

            internal void Clear()
            {
                KeyEventsList.Clear();
                MouseEventsList.Clear();
                KeyCharPressesList.Clear();
                WheelDelta = 0f;
            }

            public void CopyTo(SimpleInputSnapshot other)
            {
                Debug.Assert(this != other);

                other.MouseEventsList.Clear();
                foreach (var me in MouseEventsList) { other.MouseEventsList.Add(me); }

                other.KeyEventsList.Clear();
                foreach (var ke in KeyEventsList) { other.KeyEventsList.Add(ke); }

                other.KeyCharPressesList.Clear();
                foreach (var kcp in KeyCharPressesList) { other.KeyCharPressesList.Add(kcp); }

                other.MousePosition = MousePosition;
                other.WheelDelta = WheelDelta;
                _mouseDown.CopyTo(other._mouseDown, 0);
            }
        }
    }
}
