"""Build a measured, captioned storyboard preview. This never invents demo footage."""
import argparse
from array import array
import json
from pathlib import Path
import re
import subprocess
import sys
import textwrap
import wave

HERE = Path(__file__).resolve().parent
RATE = 48000


def run(args, cwd=HERE):
    result = subprocess.run([str(a) for a in args], cwd=cwd, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f"Command failed: {args[0]}\n{result.stderr[-5000:]}")
    return result


def probe(file):
    return json.loads(run(['ffprobe', '-v', 'error', '-show_format', '-show_streams', '-of', 'json', file]).stdout)


def loudness(file):
    result = run(['ffmpeg', '-hide_banner', '-nostdin', '-i', file, '-vn', '-af',
                  'loudnorm=I=-16:TP=-2.0:LRA=11:print_format=json', '-f', 'null', '-'])
    return json.JSONDecoder().raw_decode(result.stderr[result.stderr.rfind('{'):].strip())[0]


def timestamp(seconds, ass=False):
    multiplier = 100 if ass else 1000
    ticks = round(seconds * multiplier)
    hours, ticks = divmod(ticks, 3600 * multiplier)
    minutes, ticks = divmod(ticks, 60 * multiplier)
    sec, fraction = divmod(ticks, multiplier)
    return (f'{hours}:{minutes:02}:{sec:02}.{fraction:02}' if ass
            else f'{hours:02}:{minutes:02}:{sec:02},{fraction:03}')


def caption_chunks(text):
    words = text.split()
    chunks, current = [], []
    for word in words:
        candidate = ' '.join(current + [word])
        if current and (len(candidate) > 88 or len(current) >= 14):
            chunks.append(' '.join(current))
            current = []
        current.append(word)
    if current:
        chunks.append(' '.join(current))
    return chunks


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--work', type=Path, required=True)
    args = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=True)
    story = json.loads((HERE / 'storyboard.json').read_text())
    duration = story['durationSeconds']
    assert 0 < duration <= 120
    cursor = 0
    for scene in story['scenes']:
        assert scene['start'] == cursor and scene['end'] > scene['start']
        cursor = scene['end']
        assert (HERE / 'slides' / (scene['id'] + '.png')).exists()
    assert cursor == duration

    timeline = array('h', [0]) * int(duration * RATE)
    measurements, captions = [], []
    previous_end = 0
    for i, cue in enumerate(story['cues'], 1):
        assert previous_end <= cue['start'] < cue['end'] <= duration
        spoken = cue['text'].replace('EntraGuard', 'Entra Guard').replace('Entra ID', 'Entra I D').replace('OpenAI', 'Open A I').replace('be IT ', 'be I T ')
        aiff = args.work / f'cue-{i:02}.aiff'
        wav = args.work / f'cue-{i:02}.wav'
        run(['say', '-v', story['voice'], '-r', story['wordsPerMinute'], '-o', aiff, spoken])
        raw_duration = float(probe(aiff)['format']['duration'])
        allowed = cue['end'] - cue['start'] - 0.05
        speed = max(1.0, raw_duration / allowed)
        if speed > 1.15:
            raise ValueError(f'Cue {i} is too long ({raw_duration:.2f}s) for {allowed:.2f}s; shorten the script.')
        run(['ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', '-nostdin', '-i', aiff,
             '-af', f'atempo={speed:.8f}', '-ar', RATE, '-ac', 1, '-c:a', 'pcm_s16le', wav])
        with wave.open(str(wav), 'rb') as file:
            assert file.getframerate() == RATE and file.getnchannels() == 1 and file.getsampwidth() == 2
            pcm = array('h', file.readframes(file.getnframes()))
        if sys.byteorder != 'little':
            pcm.byteswap()
        actual = len(pcm) / RATE
        if cue['start'] + actual > cue['end'] + 0.02:
            raise ValueError(f'Cue {i} overran its window; speech will not be truncated.')
        offset = round(cue['start'] * RATE)
        timeline[offset:offset + len(pcm)] = pcm
        previous_end = cue['start'] + actual
        measurements.append({'cue': i, 'start': cue['start'], 'end': previous_end,
                             'speechDuration': actual, 'windowEnd': cue['end'], 'speedFactor': speed,
                             'text': cue['text']})
        chunks = caption_chunks(cue['text'])
        total_words = sum(len(c.split()) for c in chunks)
        position = cue['start']
        for chunk in chunks:
            end = position + actual * len(chunk.split()) / total_words
            captions.append({'start': position, 'end': end, 'text': chunk})
            position = end
        print(f'Cue {i}: {actual:.2f}s in {cue["end"] - cue["start"]:.2f}s window; speed ×{speed:.3f}')

    raw = args.work / 'narration-raw.wav'
    with wave.open(str(raw), 'wb') as file:
        file.setnchannels(1)
        file.setsampwidth(2)
        file.setframerate(RATE)
        if sys.byteorder != 'little':
            timeline.byteswap()
        file.writeframes(timeline.tobytes())
    scan = loudness(raw)
    normalize = ('loudnorm=I=-16:TP=-2.0:LRA=11:linear=true'
                 f':measured_I={scan["input_i"]}:measured_TP={scan["input_tp"]}'
                 f':measured_LRA={scan["input_lra"]}:measured_thresh={scan["input_thresh"]}'
                 f':offset={scan["target_offset"]}')
    run(['ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', '-nostdin', '-i', raw,
         '-af', normalize, '-ar', RATE, '-ac', 1, '-c:a', 'pcm_s24le', HERE / 'narration.wav'])
    (HERE / 'narration-timing.json').write_text(json.dumps({'voice': story['voice'], 'synthetic': True,
        'duration': duration, 'cues': measurements, 'captionAlignment': 'estimated within measured speech cues'}, indent=2))
    srt, dialogues = [], []
    for i, cue in enumerate(captions, 1):
        lines = textwrap.wrap(cue['text'], width=47, break_long_words=False)
        assert len(lines) <= 2, lines
        srt.append(f'{i}\n{timestamp(cue["start"])} --> {timestamp(cue["end"])}\n' + '\n'.join(lines) + '\n')
        body = r'\N'.join(lines).replace('{', '').replace('}', '')
        dialogues.append(f'Dialogue: 0,{timestamp(cue["start"], True)},{timestamp(cue["end"], True)},Default,,0,0,0,,{body}')
    (HERE / 'captions.srt').write_text('\n'.join(srt))
    (HERE / 'captions.ass').write_text('''[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 2
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,34,&H00FFFFFF,&H00FFFFFF,&H002D250C,&H002D250C,0,0,0,0,100,100,0,0,1,1,0,2,96,96,23,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
''' + '\n'.join(dialogues) + '\n')

    concat = ['ffconcat version 1.0']
    for scene in story['scenes']:
        concat += [f"file 'slides/{scene['id']}.png'", f"duration {scene['end'] - scene['start']}"]
    concat += [f"file 'slides/{story['scenes'][-1]['id']}.png'"]
    (HERE / 'visuals.ffconcat').write_text('\n'.join(concat) + '\n')
    # Letterboxed preview preserves the full slide and reserves a separate caption area.
    filters = ("fps=30,scale=1728:972,pad=1920:1080:96:0:color=0x0C252D,"
               "fade=t=in:st=0:d=0.25,fade=t=out:st=114.65:d=0.35,"
               "drawbox=x=0:y=0:w=1920:h=32:color=0x0C252D:t=fill,"
               "drawtext=font=Arial:text='STORYBOARD PREVIEW  /  DEMO FOOTAGE PENDING  /  SYNTHETIC NARRATION':"
               "fontsize=17:fontcolor=0xD5EDAE:x=(w-tw)/2:y=7,ass=captions.ass")
    output = HERE / 'EntraGuard-115s-storyboard-preview.mp4'
    run(['ffmpeg', '-y', '-hide_banner', '-loglevel', 'warning', '-nostdin',
         '-f', 'concat', '-safe', '0', '-i', HERE / 'visuals.ffconcat', '-i', HERE / 'narration.wav',
         '-vf', filters, '-t', duration, '-c:v', 'libx264', '-preset', 'medium', '-crf', '18',
         '-pix_fmt', 'yuv420p', '-r', '30', '-c:a', 'aac', '-b:a', '192k', '-ar', RATE,
         '-movflags', '+faststart', output])
    metadata = probe(output)
    video = next(s for s in metadata['streams'] if s['codec_type'] == 'video')
    audio = next(s for s in metadata['streams'] if s['codec_type'] == 'audio')
    seconds = float(metadata['format']['duration'])
    assert abs(seconds - duration) < 0.1 and seconds <= 120
    assert (video['width'], video['height'], video['pix_fmt']) == (1920, 1080, 'yuv420p')
    assert video['codec_name'] == 'h264' and video['avg_frame_rate'] == '30/1'
    assert audio['codec_name'] == 'aac' and int(audio['sample_rate']) == RATE
    final_levels = loudness(output)
    assert abs(float(final_levels['input_i']) + 16) < 1.0, final_levels
    assert float(final_levels['input_tp']) <= -1.5, final_levels
    # Decode the completed export; metadata alone cannot prove the media is readable.
    run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-nostdin', '-i', output, '-f', 'null', '-'])
    run(['ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', '-nostdin', '-ss', '70', '-i', output,
         '-frames:v', '1', HERE / 'video-check.png'])
    report = {'status': 'validated storyboard, not final submission', 'durationSeconds': seconds,
              'resolution': [video['width'], video['height']], 'frameRate': video['avg_frame_rate'],
              'videoCodec': video['codec_name'], 'pixelFormat': video['pix_fmt'], 'audioCodec': audio['codec_name'],
              'audioSampleRate': audio['sample_rate'], 'integratedLUFS': final_levels['input_i'],
              'truePeakDBTP': final_levels['input_tp'], 'narrationCues': len(measurements),
              'captions': len(captions), 'fullDecodePassed': True, 'remaining': 'Replace demo slates with real footage; review narration and align final call captions.'}
    (HERE / 'validation.json').write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
