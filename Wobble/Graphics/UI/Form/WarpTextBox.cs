using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Wobble.Assets;
using Wobble.Audio.Samples;
using Wobble.Graphics.Sprites;
using Wobble.Graphics.Sprites.Text;
using Wobble.Graphics.UI.Buttons;
using Wobble.Platform;
using Wobble.Input;

namespace Wobble.Graphics.UI.Form
{
    public class WarpTextBox : Sprite
    {
        public SpriteTextPlus Text { get; }
        public Sprite Cursor { get; }
        public Sprite SelectedSprite { get; }
        public ImageButton Button { get; }
        public Regex AllowedCharacters { get; set; } = new Regex("(.*?)");
        private string rawText;
        public string RawText
        {
            get
            {
                return rawText;
            }
            set
            {
                rawText = value;

                if (string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(PlaceholderText))
                {
                    Text.Text = PlaceholderText;
                    Text.Alpha = 0.50f;
                }
                else
                {
                    Text.Text = value;
                    Text.Alpha = 1;
                }
            }
        }
        public string PlaceholderText { get; set; }
        public int MaxCharacters { get; set; } = int.MaxValue;
        private bool focused;
        public bool Focused
        {
            get => AlwaysFocused || focused;
            set => focused = value;
        }
        public bool AlwaysFocused { get; set; }
        public bool Selected { get; set; }
        public Action<string> OnSubmit { get; set; }
        public Action<string> OnStoppedTyping { get; set; }
        public double TimeSinceCursorVisibllityChanged { get; set; }
        public int StoppedTypingActionCalltime { get; set; } = 500;
        public bool AllowSubmission { get; set; } = true;
        public bool ClearOnSubmission { get; set; } = true;
        private double TimeSinceStoppedTyping { get; set; }
        private bool FiredStoppedTypingActionHandlers { get; set; } = true;
        private Clipboard Clipboard { get; } = Clipboard.NativeClipboard;
        private static List<AudioSample> keyClickSamples;
        public static List<AudioSample> KeyClickSamples
        {
            get => keyClickSamples;
            set
            {
                keyClickSamples?.ForEach(x => x.Dispose());
                keyClickSamples = value;
            }
        }
        private bool EnableKeyClickSounds { get; set; } = true;
        private Random Rng = new Random();

        public WarpTextBox(ScalableVector2 size, WobbleFontStore font, int fontSize, string initialText = "", string placeHolderText = "")
        {
            Size = size;
            rawText = initialText ?? "";
            PlaceholderText = placeHolderText ?? "";
            Text = new SpriteTextPlus(font, rawText, fontSize)
            {
                Parent = this,
                Alignment = Alignment.MidLeft,
                X = 10,
                Y = 0,
                Text = rawText
            };

            Cursor = new Sprite()
            {
                Parent = this,
                Alignment = Alignment.MidLeft,
                Size = new ScalableVector2(2, Text.Height),
                Tint = Color.White,
                Visible = false
            };

            SelectedSprite = new Sprite()
            {
                Parent = this,
                Alignment = Alignment.MidLeft,
                Size = new ScalableVector2(Width * 0.98f, Height * 0.85f),
                Tint = Color.White,
                Alpha = 0,
                Y = 1,
                X = Text.X - 1
            };

            Button = new ImageButton(WobbleAssets.WhiteBox)
            {
                Parent = this,
                Size = Size,
                Alpha = 0.5f
            };
            Button.Clicked += (o, e) =>
            {
                focused = true;
            };
            Button.ClickedOutside += (o, e) =>
            {
                focused = false;
            };

            ChangeCursorLocation();

            GameBase.Game.Window.TextInput += OnTextInputEntered;
        }

        public override void Update(GameTime gameTime)
        {
            TimeSinceStoppedTyping += gameTime.ElapsedGameTime.TotalMilliseconds;

            // Handle when the user stops typing. and invoke the action handlers.
            if (TimeSinceStoppedTyping >= StoppedTypingActionCalltime && !FiredStoppedTypingActionHandlers)
            {
                OnStoppedTyping?.Invoke(rawText);
                FiredStoppedTypingActionHandlers = true;
            }

            // Handle all input.
            HandleCtrlInput();
            HandleEnter();
            ChangeCursorLocation();

            // Change the alpha of the selected sprite depending on if we're currently in a CTRL+A operation.
            SelectedSprite.Alpha = MathHelper.Lerp(SelectedSprite.Alpha, Selected ? 0.25f : 0,
                (float)Math.Min(gameTime.ElapsedGameTime.TotalMilliseconds / 60, 1));

            PerformCursorBlinking(gameTime);

            base.Update(gameTime);
        }

        public override void Destroy()
        {
            GameBase.Game.Window.TextInput -= OnTextInputEntered;
            base.Destroy();
        }

        private void OnTextInputEntered(object sender, TextInputEventArgs e)
        {
            if (!focused)
                return;

            // On Linux this gets sent on switching the keyboard layout.
            if (e.Character == '\0')
                return;

            // On Linux some characters (like Backspace, plus or minus) get sent here even when CTRL is down, and we
            // don't handle that here.
            if (KeyboardManager.CurrentState.IsKeyDown(Keys.LeftControl)
                || KeyboardManager.CurrentState.IsKeyDown(Keys.RightControl))
                return;

            // Enter is handled in Update() because TextInput only receives the regular Enter and not the NumPad Enter.
            if (e.Key == Keys.Enter)
                return;

            // If the text is selected (in a CTRL+A) operation
            if (Selected)
            {
                // Clear text
                rawText = "";

                switch (e.Key)
                {
                    case Keys.Back:
                    case Keys.Tab:
                    case Keys.Delete:
                    case Keys.VolumeUp:
                    case Keys.VolumeDown:
                        break;
                    // For all other key presses, we reset the string and append the new character
                    default:
                        if (rawText.Length + 1 <= MaxCharacters)
                        {
                            var proposedText = rawText + e.Character;

                            if (!AllowedCharacters.IsMatch(proposedText))
                                return;

                            rawText += proposedText;
                        }
                        break;
                }

                Selected = false;
            }
            // Handle normal key presses.
            else
            {
                // Handle key inputs.
                switch (e.Key)
                {
                    // Ignore these keys
                    case Keys.Tab:
                    case Keys.Delete:
                    case Keys.Escape:
                    case Keys.VolumeUp:
                    case Keys.VolumeDown:
                        return;
                    // Back spacing
                    case Keys.Back:
                        if (string.IsNullOrEmpty(rawText))
                            return;

                        var charStartIndices = StringInfo.ParseCombiningCharacters(rawText);
                        rawText = rawText.Remove(charStartIndices.Last());
                        PlayKeyClickSound();
                        break;
                    // Input text
                    default:
                        if (rawText.Length + 1 <= MaxCharacters)
                        {
                            var proposedText = rawText + e.Character;

                            if (!AllowedCharacters.IsMatch(proposedText))
                                return;

                            rawText = proposedText;
                            PlayKeyClickSound();
                        }
                        break;
                }
            }

            ReadjustTextbox();
        }

        private void ChangeCursorLocation()
        {
            if (string.IsNullOrEmpty(rawText))
            {
                Cursor.X = Text.X;
                return;
            }

            Cursor.X = Text.X + Text.Width;
            SelectedSprite.Width = Cursor.X;
        }

        private void PerformCursorBlinking(GameTime gameTime)
        {
            if (!focused)
            {
                Cursor.Visible = false;
                return;
            }

            TimeSinceCursorVisibllityChanged += gameTime.ElapsedGameTime.TotalMilliseconds;

            if (!(TimeSinceCursorVisibllityChanged >= 500))
                return;

            Cursor.Visible = !Cursor.Visible;
            TimeSinceCursorVisibllityChanged = 0;
        }

        public void ReadjustTextbox()
        {
            // Make cursor visible and reset its visiblity changing.
            Cursor.Visible = true;
            TimeSinceCursorVisibllityChanged = 0;
            TimeSinceStoppedTyping = 0;

            FiredStoppedTypingActionHandlers = false;
        }

        private void HandleCtrlInput()
        {
            // Make sure the textbox is focused and that the control buttons are down before handling anything.
            if (!focused || (!KeyboardManager.CurrentState.IsKeyDown(Keys.LeftControl)
                && !KeyboardManager.CurrentState.IsKeyDown(Keys.RightControl)))
                return;

            // CTRL+A, Select the text.
            if (KeyboardManager.IsUniqueKeyPress(Keys.A) && !string.IsNullOrEmpty(rawText))
                Selected = true;

            // CTRL+C, Copy the text to the clipboard.
            if (KeyboardManager.IsUniqueKeyPress(Keys.C) && Selected)
                Clipboard.SetText(rawText);

            // CTRL+X, Cut the text to the clipboard.
            if (KeyboardManager.IsUniqueKeyPress(Keys.X) && Selected)
            {
                Clipboard.SetText(rawText);
                rawText = "";

                ReadjustTextbox();
                Selected = false;
            }

            // CTRL+V Paste text
            if (KeyboardManager.IsUniqueKeyPress(Keys.V))
            {
                var clipboardText = Clipboard.GetText().Replace("\n", "").Replace("\r", "");

                if (!string.IsNullOrEmpty(clipboardText))
                {
                    if (Selected)
                    {
                        if (!AllowedCharacters.IsMatch(clipboardText))
                            return;

                        rawText = clipboardText;
                    }
                    else
                    {
                        var proposed = rawText + clipboardText;

                        if (!AllowedCharacters.IsMatch(proposed))
                            return;

                        rawText = proposed;
                    }
                }

                ReadjustTextbox();
                Selected = false;
            }

            // CTRL+W or CTRL+Backspace: kill word backwards.
            // This means killing all trailing whitespace and then all trailing non-whitespace.
            if (KeyboardManager.IsUniqueKeyPress(Keys.W) || KeyboardManager.IsUniqueKeyPress(Keys.Back))
            {
                if (Selected)
                {
                    // When everything is selected we act as a normal backspace and delete everything
                    rawText = "";
                }
                else
                {
                    var withoutTrailingWhitespace = rawText.TrimEnd();
                    var nonWhitespacesInTheEnd = withoutTrailingWhitespace.ToCharArray()
                        .Select(c => c).Reverse().TakeWhile(c => !char.IsWhiteSpace(c)).Count();
                    rawText = withoutTrailingWhitespace.Substring(0,
                        withoutTrailingWhitespace.Length - nonWhitespacesInTheEnd);
                }

                ReadjustTextbox();
                Selected = false;
            }

            // Ctrl+U: kill line backwards.
            // Delete from the cursor position to the start of the line.
            if (KeyboardManager.IsUniqueKeyPress(Keys.U))
            {
                // Since we don't have a concept of a cursor, simply delete the whole text.
                rawText = "";

                ReadjustTextbox();
                Selected = false;
            }
        }

        private void HandleEnter()
        {
            if (KeyboardManager.IsUniqueKeyPress(Keys.Enter))
            {
                if (!AllowSubmission)
                    return;

                if (string.IsNullOrEmpty(rawText))
                    return;

                // Run the callback function that was passed in.
                OnSubmit?.Invoke(rawText);

                // Clear text box.
                if (ClearOnSubmission)
                {
                    rawText = "";
                    Selected = false;
                    ReadjustTextbox();
                }
            }
        }

        private void PlayKeyClickSound()
        {
            if (keyClickSamples == null)
                return;

            if (!EnableKeyClickSounds || keyClickSamples.Count == 0)
                return;

            var r = Rng.Next(keyClickSamples.Count);
            keyClickSamples[r].CreateChannel().Play();
        }
    }
}