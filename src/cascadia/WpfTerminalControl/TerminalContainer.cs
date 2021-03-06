﻿// <copyright file="TerminalContainer.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
// </copyright>

namespace Microsoft.Terminal.Wpf
{
    using System;
    using System.Runtime.InteropServices;
    using System.Windows;
    using System.Windows.Interop;
    using System.Windows.Media;
    using System.Windows.Threading;

    /// <summary>
    /// The container class that hosts the native hwnd terminal.
    /// </summary>
    /// <remarks>
    /// This class is only left public since xaml cannot work with internal classes.
    /// </remarks>
    public class TerminalContainer : HwndHost
    {
        private ITerminalConnection connection;
        private IntPtr hwnd;
        private IntPtr terminal;
        private DispatcherTimer blinkTimer;
        private NativeMethods.ScrollCallback scrollCallback;
        private NativeMethods.WriteCallback writeCallback;

        /// <summary>
        /// Initializes a new instance of the <see cref="TerminalContainer"/> class.
        /// </summary>
        public TerminalContainer()
        {
            this.MessageHook += this.TerminalContainer_MessageHook;
            this.GotFocus += this.TerminalContainer_GotFocus;
            this.Focusable = true;

            var blinkTime = NativeMethods.GetCaretBlinkTime();

            if (blinkTime != uint.MaxValue)
            {
                this.blinkTimer = new DispatcherTimer();
                this.blinkTimer.Interval = TimeSpan.FromMilliseconds(blinkTime);
                this.blinkTimer.Tick += (_, __) =>
                {
                    if (this.terminal != IntPtr.Zero)
                    {
                        NativeMethods.TerminalBlinkCursor(this.terminal);
                    }
                };
            }
        }

        /// <summary>
        /// Event that is fired when the terminal buffer scrolls from text output.
        /// </summary>
        internal event EventHandler<(int viewTop, int viewHeight, int bufferSize)> TerminalScrolled;

        /// <summary>
        /// Event that is fired when the user engages in a mouse scroll over the terminal hwnd.
        /// </summary>
        internal event EventHandler<int> UserScrolled;

        /// <summary>
        /// Gets or sets a value indicating whether if the renderer should automatically resize to fill the control
        /// on user action.
        /// </summary>
        public bool AutoFill { get; set; } = true;

        /// <summary>
        /// Gets the current character rows available to the terminal.
        /// </summary>
        internal int Rows { get; private set; }

        /// <summary>
        /// Gets the current character columns available to the terminal.
        /// </summary>
        internal int Columns { get; private set; }

        /// <summary>
        /// Gets the maximum amount of character rows that can fit in this control.
        /// </summary>
        /// <remarks>This will be in sync with <see cref="Rows"/> unless <see cref="AutoFill"/> is set to false.</remarks>
        internal int MaxRows { get; private set; }

        /// <summary>
        /// Gets the maximum amount of character columns that can fit in this control.
        /// </summary>
        /// <remarks>This will be in sync with <see cref="Columns"/> unless <see cref="AutoFill"/> is set to false.</remarks>
        internal int MaxColumns { get; private set; }

        /// <summary>
        /// Gets the window handle of the terminal.
        /// </summary>
        internal IntPtr Hwnd => this.hwnd;

        /// <summary>
        /// Sets the connection to the terminal backend.
        /// </summary>
        internal ITerminalConnection Connection
        {
            private get
            {
                return this.connection;
            }

            set
            {
                if (this.connection != null)
                {
                    this.connection.TerminalOutput -= this.Connection_TerminalOutput;
                }

                this.connection = value;
                this.connection.TerminalOutput += this.Connection_TerminalOutput;
                this.connection.Start();
            }
        }

        /// <summary>
        /// Manually invoke a scroll of the terminal buffer.
        /// </summary>
        /// <param name="viewTop">The top line to show in the terminal.</param>
        internal void UserScroll(int viewTop)
        {
            NativeMethods.TerminalUserScroll(this.terminal, viewTop);
        }

        /// <summary>
        /// Sets the theme for the terminal. This includes font family, size, color, as well as background and foreground colors.
        /// </summary>
        /// <param name="theme">The color theme for the terminal to use.</param>
        /// <param name="fontFamily">The font family to use in the terminal.</param>
        /// <param name="fontSize">The font size to use in the terminal.</param>
        internal void SetTheme(TerminalTheme theme, string fontFamily, short fontSize)
        {
            var dpiScale = VisualTreeHelper.GetDpi(this);

            NativeMethods.TerminalSetTheme(this.terminal, theme, fontFamily, fontSize, (int)dpiScale.PixelsPerInchX);

            this.TriggerResize(this.RenderSize);
        }

        /// <summary>
        /// Gets the selected text from the terminal renderer and clears the selection.
        /// </summary>
        /// <returns>The selected text, empty if no text is selected.</returns>
        internal string GetSelectedText()
        {
            if (NativeMethods.TerminalIsSelectionActive(this.terminal))
            {
                return NativeMethods.TerminalGetSelection(this.terminal);
            }

            return string.Empty;
        }

        /// <summary>
        /// Triggers a refresh of the terminal with the given size.
        /// </summary>
        /// <param name="renderSize">Size of the rendering window.</param>
        /// <returns>Tuple with rows and columns.</returns>
        internal (int rows, int columns) TriggerResize(Size renderSize)
        {
            var dpiScale = VisualTreeHelper.GetDpi(this);

            NativeMethods.COORD dimensions;
            NativeMethods.TerminalTriggerResize(
                this.terminal,
                Convert.ToInt16(renderSize.Width * dpiScale.DpiScaleX),
                Convert.ToInt16(renderSize.Height * dpiScale.DpiScaleY),
                out dimensions);

            this.Rows = dimensions.Y;
            this.Columns = dimensions.X;

            this.Connection?.Resize((uint)dimensions.Y, (uint)dimensions.X);
            return (dimensions.Y, dimensions.X);
        }

        /// <summary>
        /// Resizes the terminal using row and column count as the new size.
        /// </summary>
        /// <param name="rows">Number of rows to show.</param>
        /// <param name="columns">Number of columns to show.</param>
        /// <returns><see cref="long"/> pair with the new width and height size in pixels for the renderer.</returns>
        internal (int width, int height) Resize(uint rows, uint columns)
        {
            NativeMethods.SIZE dimensionsInPixels;
            NativeMethods.COORD dimensions = new NativeMethods.COORD
            {
                X = (short)columns,
                Y = (short)rows,
            };

            NativeMethods.TerminalTriggerResizeWithDimension(this.terminal, dimensions, out dimensionsInPixels);

            // If AutoFill is true, keep Rows and Columns in sync with MaxRows and MaxColumns.
            // Otherwise, MaxRows and MaxColumns will be set on startup and on control resize by the user.
            if (this.AutoFill)
            {
                this.MaxColumns = dimensions.X;
                this.MaxRows = dimensions.Y;
            }

            this.Columns = dimensions.X;
            this.Rows = dimensions.Y;

            this.Connection?.Resize((uint)dimensions.Y, (uint)dimensions.X);

            return (dimensionsInPixels.cx, dimensionsInPixels.cy);
        }

        /// <inheritdoc/>
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            if (this.terminal != IntPtr.Zero)
            {
                NativeMethods.TerminalDpiChanged(this.terminal, (int)(NativeMethods.USER_DEFAULT_SCREEN_DPI * newDpi.DpiScaleX));
            }
        }

        /// <inheritdoc/>
        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            var dpiScale = VisualTreeHelper.GetDpi(this);
            NativeMethods.CreateTerminal(hwndParent.Handle, out this.hwnd, out this.terminal);

            this.scrollCallback = this.OnScroll;
            this.writeCallback = this.OnWrite;

            NativeMethods.TerminalRegisterScrollCallback(this.terminal, this.scrollCallback);
            NativeMethods.TerminalRegisterWriteCallback(this.terminal, this.writeCallback);

            // If the saved DPI scale isn't the default scale, we push it to the terminal.
            if (dpiScale.PixelsPerInchX != NativeMethods.USER_DEFAULT_SCREEN_DPI)
            {
                NativeMethods.TerminalDpiChanged(this.terminal, (int)dpiScale.PixelsPerInchX);
            }

            if (NativeMethods.GetFocus() == this.hwnd)
            {
                this.blinkTimer?.Start();
            }
            else
            {
                NativeMethods.TerminalSetCursorVisible(this.terminal, false);
            }

            return new HandleRef(this, this.hwnd);
        }

        /// <inheritdoc/>
        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            NativeMethods.DestroyTerminal(this.terminal);
            this.terminal = IntPtr.Zero;
        }

        private static void UnpackKeyMessage(IntPtr wParam, IntPtr lParam, out ushort vkey, out ushort scanCode, out ushort flags)
        {
            ulong scanCodeAndFlags = (((ulong)lParam) & 0xFFFF0000) >> 16;
            scanCode = (ushort)(scanCodeAndFlags & 0x00FFu);
            flags = (ushort)(scanCodeAndFlags & 0xFF00u);
            vkey = (ushort)wParam;
        }

        private static void UnpackCharMessage(IntPtr wParam, IntPtr lParam, out char character, out ushort scanCode, out ushort flags)
        {
            UnpackKeyMessage(wParam, lParam, out ushort vKey, out scanCode, out flags);
            character = (char)vKey;
        }

        private void TerminalContainer_GotFocus(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            NativeMethods.SetFocus(this.hwnd);
        }

        private IntPtr TerminalContainer_MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (hwnd == this.hwnd)
            {
                switch ((NativeMethods.WindowMessage)msg)
                {
                    case NativeMethods.WindowMessage.WM_SETFOCUS:
                        NativeMethods.TerminalSetFocus(this.terminal);
                        this.blinkTimer?.Start();
                        break;
                    case NativeMethods.WindowMessage.WM_KILLFOCUS:
                        NativeMethods.TerminalKillFocus(this.terminal);
                        this.blinkTimer?.Stop();
                        NativeMethods.TerminalSetCursorVisible(this.terminal, false);
                        break;
                    case NativeMethods.WindowMessage.WM_MOUSEACTIVATE:
                        this.Focus();
                        NativeMethods.SetFocus(this.hwnd);
                        break;
                    case NativeMethods.WindowMessage.WM_SYSKEYDOWN: // fallthrough
                    case NativeMethods.WindowMessage.WM_KEYDOWN:
                        {
                            // WM_KEYDOWN lParam layout documentation: https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-keydown
                            NativeMethods.TerminalSetCursorVisible(this.terminal, true);

                            UnpackKeyMessage(wParam, lParam, out ushort vkey, out ushort scanCode, out ushort flags);
                            NativeMethods.TerminalSendKeyEvent(this.terminal, vkey, scanCode, flags, true);
                            this.blinkTimer?.Start();
                            break;
                        }

                    case NativeMethods.WindowMessage.WM_SYSKEYUP: // fallthrough
                    case NativeMethods.WindowMessage.WM_KEYUP:
                        {
                            // WM_KEYUP lParam layout documentation: https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-keyup
                            UnpackKeyMessage(wParam, lParam, out ushort vkey, out ushort scanCode, out ushort flags);
                            NativeMethods.TerminalSendKeyEvent(this.terminal, (ushort)wParam, scanCode, flags, false);
                            break;
                        }

                    case NativeMethods.WindowMessage.WM_CHAR:
                        {
                            // WM_CHAR lParam layout documentation: https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-char
                            UnpackCharMessage(wParam, lParam, out char character, out ushort scanCode, out ushort flags);
                            NativeMethods.TerminalSendCharEvent(this.terminal, character, scanCode, flags);
                            break;
                        }

                    case NativeMethods.WindowMessage.WM_WINDOWPOSCHANGED:
                        var windowpos = (NativeMethods.WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(NativeMethods.WINDOWPOS));
                        if (((NativeMethods.SetWindowPosFlags)windowpos.flags).HasFlag(NativeMethods.SetWindowPosFlags.SWP_NOSIZE))
                        {
                            break;
                        }

                        NativeMethods.COORD dimensions;

                        // We only trigger a resize if we want to fill to maximum size.
                        if (this.AutoFill)
                        {
                            NativeMethods.TerminalTriggerResize(this.terminal, (short)windowpos.cx, (short)windowpos.cy, out dimensions);

                            this.Columns = dimensions.X;
                            this.Rows = dimensions.Y;
                            this.MaxColumns = dimensions.X;
                            this.MaxRows = dimensions.Y;
                        }
                        else
                        {
                            NativeMethods.TerminalCalculateResize(this.terminal, (short)windowpos.cx, (short)windowpos.cy, out dimensions);
                            this.MaxColumns = dimensions.X;
                            this.MaxRows = dimensions.Y;
                        }

                        this.Connection?.Resize((uint)dimensions.Y, (uint)dimensions.X);
                        break;

                    case NativeMethods.WindowMessage.WM_MOUSEWHEEL:
                        var delta = (short)(((long)wParam) >> 16);
                        this.UserScrolled?.Invoke(this, delta);
                        break;
                }
            }

            return IntPtr.Zero;
        }

        private void LeftClickHandler(int lParam)
        {
            var altPressed = NativeMethods.GetKeyState((int)NativeMethods.VirtualKey.VK_MENU) < 0;
            var x = (short)(((int)lParam << 16) >> 16);
            var y = (short)((int)lParam >> 16);
            NativeMethods.COORD cursorPosition = new NativeMethods.COORD()
            {
                X = x,
                Y = y,
            };

            NativeMethods.TerminalStartSelection(this.terminal, cursorPosition, altPressed);
        }

        private void MouseMoveHandler(int wParam, int lParam)
        {
            if (((int)wParam & 0x0001) == 1)
            {
                var x = (short)(((int)lParam << 16) >> 16);
                var y = (short)((int)lParam >> 16);
                NativeMethods.COORD cursorPosition = new NativeMethods.COORD()
                {
                    X = x,
                    Y = y,
                };
                NativeMethods.TerminalMoveSelection(this.terminal, cursorPosition);
            }
        }

        private void Connection_TerminalOutput(object sender, TerminalOutputEventArgs e)
        {
            if (this.terminal != IntPtr.Zero)
            {
                NativeMethods.TerminalSendOutput(this.terminal, e.Data);
            }
        }

        private void OnScroll(int viewTop, int viewHeight, int bufferSize)
        {
            this.TerminalScrolled?.Invoke(this, (viewTop, viewHeight, bufferSize));
        }

        private void OnWrite(string data)
        {
            this.Connection?.WriteInput(data);
        }
    }
}
