# ZeppieSays

Local voice dictation for Windows, in the spirit of Wispr Flow, but the models run on your own machine
unless you choose a cloud one.
Press a key anywhere, talk, and the text lands where your cursor is.

## What it does

- **Two dictation keys**, each bound to its own model and language (for example F8 → Whisper in Dutch,
  F9 → Parakeet in English). Hold the key to talk, or tap it once to keep listening and tap again to finish.
  Esc cancels.
- **Local models.** Install them from the Local models tab:
  - Whisper (whisper.cpp ggml files, run on an NVIDIA GPU through CUDA 12, CPU as fallback):
    large-v3, large-v3 turbo, turbo compact, medium (English), small. You can also add your own `.bin` file.
  - NVIDIA Parakeet TDT 0.6B v2 (English) and v3 (25 European languages, including Dutch), run on the CPU
    through sherpa-onnx.
- **ElevenLabs, optional, in the cloud.** Save an ElevenLabs API key in Settings, AI providers, and
  *ElevenLabs Scribe v2 (cloud)* can be picked for a dictation key or the Notetaker. Your audio is sent to
  ElevenLabs. Dictionary words (and names from the screen, when dictating) go along as key terms, at most 100,
  which ElevenLabs charges 20% extra for.
- **Ask a question.** Set a key's "What it does" to *Ask a question*, hold it and ask ("what does this word mean?",
  "explain this", "what's the latest .NET?"). The answer streams into a card above the pill, with Copy and Paste at
  cursor; Esc or × closes it. The selected text, the text around the cursor and the window's text (OCR) go along with
  the question. Each Ask key has its own answering model (kept in the profile): Claude and OpenAI search the web when
  needed and show their sources; Ollama answers on this PC without searching.
- **A small pill** at the bottom of the screen shows the key, the waveform and the model while you talk.
  The finished text is inserted in one go when you stop.
- **Dictionary.** Words you use a lot get the right spelling. `Betabit` alone works, and
  `Betabit = beta bit, beta bid` also fixes spellings the model keeps getting wrong. Words live in plain text
  files, one per line: `ZeppieSaysDictionary.txt` in the settings folder, plus any others you add (say, a team
  list in a shared folder). Click a file on the Dictionary page to edit its words there; edits made elsewhere
  are picked up as soon as the file is saved.
- **Settings folder.** Settings, dictionary, voices and stats can live in a folder you choose, such as OneDrive,
  so they survive a reinstall and follow you to another PC. API keys always stay on the PC (`keys.json`).
- **Screen context, on this PC.** When you start dictating, ZeppieSays reads the text around your cursor
  (Windows UI Automation) and the words visible in the active window (built-in Windows OCR). Code-like
  names such as `userId` or `Program.cs` are passed to Whisper as vocabulary, and in code editors
  (VS Code, Cursor, Visual Studio, Rider, …) a spoken "user id" becomes `userId`.
- **Rule-based cleanup**: filler words (uh, um, eh, euh), capitalization that follows the surrounding
  sentence, spacing, and silence detection.
- **Styles per app or website**: an optional LLM pass that rewrites the dictation for where it goes,
  e.g. Casual in Teams, Formal in Outlook and Gmail, Prompt in Claude or ChatGPT, Raw everywhere else.
  Each style has a strength (light, medium, strong), rules in plain words and a Try box. All styles use one
  default model (so a local model never has to be swapped in and out of graphics memory), unless a style
  picks its own, e.g. a cloud model for Formal. The default model is loaded when ZeppieSays starts and kept
  loaded for a configurable time (30 minutes to always).
- **Profiles** hold every model choice: the speech model and spoken language of each dictation key, the default
  AI model and each style's model. Switch all of them at once (say Local, Cloud and Test) from the top of the
  sidebar or the tray menu. The hotkeys, the language they type in, the dictionary, the styles' rules and the
  Notetaker are the same in every profile.
  Models run locally through [Ollama](https://ollama.com) (default `qwen3:4b-instruct`, ~0.5 s per rewrite
  on an RTX 4060), or in the cloud through OpenAI or Anthropic with your own API key (stored encrypted with
  Windows DPAPI; such styles are clearly marked). The rewrite must keep your language and meaning; if it
  is slow, fails, or looks like an answer instead of a rewrite, the plain transcript is pasted.
  The tray menu can copy the last dictation unedited.

- **Notetaker.** Every audio or video file saved in (or dropped onto) the Notetaker folder
  (default `Documents\Zeppie Says\Notes`) is transcribed with timestamps by the model you pick. Optional: speaker
  labels (local sherpa-onnx diarization, two small models downloaded on first use; pick the number of people when
  you know it), a summary with decisions and action items from your default style model, and dictionary hints for
  Whisper and ElevenLabs. The transcript is written as Markdown next to the audio, and both move to `Done`. Transcribing pauses
  while you dictate.
- **Meeting recording.** When Teams, Zoom, Webex, Slack or Discord starts using the microphone (Windows' own
  microphone-usage record), a corner pop-up asks whether to record; *Record now* on the Notetaker page works for
  anything else. Your microphone and the call's sound are recorded as separate tracks (left/right of an .m4a), so
  everything on the microphone track is labelled with your name, minus echo of the call; only the call track needs
  speaker detection. Recording stops when the meeting app lets go of the microphone. Tell the others you're recording.
- **Voice library.** Name a speaker under a transcript ("Speaker 2" → Anna) and the transcript is updated; the voice
  is remembered (`voices.json`, voiceprints only) and recognised in later recordings. Your own voice is learned from
  the microphone track of every meeting.

Text is inserted by pasting; your clipboard is restored right after, and dictations are kept out of
Windows clipboard history.

## Build and run

Requires the .NET 10 SDK on Windows 10/11 (x64). An NVIDIA GPU with the CUDA 12 toolkit is optional but
makes Whisper about five times faster (a 10-second sentence takes ~0.5 s on an RTX 4060).

Model notes from testing: Whisper large-v3 turbo is the best Dutch choice because it takes the dictionary
and screen context as hints. Parakeet is fast on the CPU and excellent for English, but it can't take
hints, so for Parakeet add `Term = wrong spelling` entries to the dictionary. ElevenLabs Scribe v2 takes the
dictionary as key terms and got `ZeppieSays` right in ~0.9 s for a 3-second sentence.

```bash
dotnet build ZeppieSays.slnx
dotnet run --project src/ZeppieSays
```

ZeppieSays lives in the system tray. Closing the window keeps it running; use the tray menu to quit.

Settings, models and logs are stored in `%LOCALAPPDATA%\ZeppieSays`.

## Project layout

| Path | What's there |
| --- | --- |
| `src/ZeppieSays/Core` | Settings, model catalog, paths and logging |
| `src/ZeppieSays/Engines` | Whisper.net, sherpa-onnx and ElevenLabs engines, plus a cache that keeps bound models loaded |
| `src/ZeppieSays/Services` | Key hook, recording, dictation flow, screen context, text cleanup, pasting, model downloads |
| `src/ZeppieSays/Notes` | Notetaker: audio file decoding, speaker diarization, the transcription queue, summaries |
| `src/ZeppieSays/UI` | Settings view models and the floating overlay |
| `tests/ZeppieSays.Tests` | Unit tests for the text cleanup and identifier extraction |
| `tools/ZeppieSays.Probe` | Developer tool: installs models and transcribes synthesized speech without a microphone |
| `tools/make-icon.py` | Regenerates the app icon and logo from `tools/zeppie-logo-source.webp` (needs Pillow) |

Probe examples:

```bash
dotnet run --project tools/ZeppieSays.Probe -- install whisper-large-v3-turbo
dotnet run --project tools/ZeppieSays.Probe -- run whisper-large-v3-turbo nl "Ik werk bij Betabit aan ZeppieSays." "Betabit;ZeppieSays"
dotnet run --project tools/ZeppieSays.Probe -- run elevenlabs-scribe-v2 nl "Ik werk bij Betabit aan ZeppieSays." "Betabit;ZeppieSays"
dotnet run --project tools/ZeppieSays.Probe -- diarize meeting.m4a 0.4 0.5 n2
```
