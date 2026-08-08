# HappyBot

HappyBot is a C#/.NET 10 WinForms rewrite of the original `HappyBot.ahk`. It provides screen-based attack detection, guard automation, parry, deflect, dodge, and hero-specific responses.

The verified input path is a merged virtual Xbox controller:

```text
DS4Windows controller -> HappyBot state merge -> one ViGEm Xbox controller -> game
```

This avoids the input-device switching lag caused by exposing both the source controller and the bot controller to the game.

## Requirements

- Windows 10/11 64-bit
- .NET 10 SDK
- ViGEmBus
- DS4Windows when using a PlayStation 4 controller
- HidHide when using DS4Windows and the merged-controller path

## Build

```powershell
dotnet restore
dotnet build HappyBot.csproj --configuration Debug
```

The executable is written to:

```text
bin\Debug\net10.0-windows\HappyBot.exe
```

## Run With ViGEm

Use `HappyBot-ViGEm.bat` from the repository root after building:

```powershell
.\HappyBot-ViGEm.bat
```

The launcher sets `HAPPYBOT_INPUT_MODE=vigem` and starts the built executable.

Recommended startup order:

1. Connect the DS4 and start DS4Windows using an Xbox 360 profile.
2. Start HidHide with the source devices hidden from the game.
3. Run `HappyBot-ViGEm.bat`.
4. Confirm the status shows `V:ON` and `Src:ON`.
5. Start or restart the game after the virtual controller exists.

The bot does not need administrator rights in ViGEm mode. Driver installation and HidHide configuration require administrator rights.

## HidHide Setup

Add these applications to HidHide's whitelist:

- `HappyBot.exe`
- `DS4Windows.exe`

Do not whitelist the game. Hide the physical Sony controller and the DS4Windows virtual Xbox controller so the game sees only HappyBot's merged output controller. Reconnect the controller after changing HidHide settings.

## Controller Mapping

The verified in-game Xbox layout is:

| Controller input | Game action |
| --- | --- |
| LT | Guard mode |
| LB | Quick chat |
| RT | Heavy attack / parry |
| RB | Light attack |
| X | Guardbreak |
| B | Cancel heavy attack |
| A | Dodge |
| Left stick | Movement |
| Right stick | Guard direction |

HappyBot's internal output mapping is:

| Bot action | Virtual controller output |
| --- | --- |
| Space | A |
| Left mouse button | RB |
| Right mouse button | RT |
| Numpad 5 | X |
| C / Numpad 9 | LT |
| Numpad 4 / 6 / 8 | Right stick left / right / up |
| Arrow keys | Left stick |

The configured hold button is `LT`, which also drives the bot's F-button response path.

## Using The Menu

1. Set the game resolution in the HappyBot window.
2. Select the hero being played.
3. Enable only the features needed for the test.
4. Click **Start** to begin the bot.

Checkbox changes apply while the bot is running. The **Apply Settings** button is not required for checkbox changes.

Important feature switches:

- **Auto Block** controls automatic guard direction changes.
- **Auto Parry** in the F section controls the generic F/LT parry path.
- **Auto Parry** in the E section controls the E-key path.
- **Auto Deflect** controls directional dodge deflects.
- **Unblockables** controls unblockable detection and dodge responses.
- **Your Hero** enables hero-specific F/LT counters.
- **No Hero** suppresses hero-specific behavior.

The status line exposes the active settings as `P1`, `P2`, and `U`. `F:DOWN` confirms the configured hold button is detected.

## Test Input

Click **Test Input** to cycle through diagnostic actions:

- Dodge (A)
- Heavy (RT)
- Light (RB)
- Guard Break (X)
- Guard Top
- Guard Left
- Guard Right

Each test sends five inputs after a three-second delay.

## Legacy Input

`InterceptionInput.cs` and `lib\interception.dll` remain in the source for compatibility with the earlier input path. The verified setup uses ViGEm and does not require the Interception driver.

## Notes

- The original behavior reference is preserved in `HappyBot.ahk`.
- Build output and generated `Config.ini` files are ignored by Git.
- Game automation can violate game rules or anti-cheat policies. Test responsibly, preferably in training or offline modes.
