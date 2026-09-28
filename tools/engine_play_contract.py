"""Bounded opt-in normal-input gameplay extension to hidden capture-v1.

These records describe observations, not success criteria for a particular map.
"""
from __future__ import annotations
import math
import re
import hashlib


def validate_gameplay(value, timeout_ms):
    # Delayed import avoids a cycle with the launcher which owns staging/supervision.
    from invoke_engine_capture import exact_keys, integer, number, require
    exact_keys(value, {'schemaVersion','moverThing','cog','goalBin','symbol','steps'}, set(), 'gameplay')
    require(type(value['schemaVersion']) is int and value['schemaVersion'] == 1, 'Unsupported gameplay schemaVersion.')
    result = {'schemaVersion':1,
              'moverThing':integer(value['moverThing'],0,1000000,'moverThing'),
              'cog':integer(value['cog'],0,1000000,'cog'),
              'goalBin':integer(value['goalBin'],100,115,'goalBin'), 'symbol':value['symbol'], 'steps':[]}
    require(isinstance(value['symbol'],str) and re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]{0,62}',value['symbol']),
            'symbol must be a bounded COG identifier.')
    require(isinstance(value['steps'],list) and 1 <= len(value['steps']) <= 32, 'gameplay needs 1..32 steps.')
    total = 0
    for step in value['steps']:
        exact_keys(step, {'durationMs','forward','strafe','turn','pitch','activate'}, set(), 'gameplay step')
        item = {'durationMs':integer(step['durationMs'],50,5000,'durationMs')}
        total += item['durationMs']
        for axis in ('forward','strafe','turn','pitch'):
            item[axis] = number(step[axis],-1,1,axis)
        item['activate'] = integer(step['activate'],0,1,'activate')
        result['steps'].append(item)
    require(total <= 30000, 'Gameplay duration exceeds30 seconds.')
    require(total + 2000 <= timeout_ms, 'Engine timeout must leave at least2 seconds beyond gameplay steps.')
    return result


def gameplay_text(play, target):
    from invoke_engine_capture import require
    path = str(target)
    require(target.is_absolute() and target.suffix == '.json' and len(path) <= 1023
            and path.isascii() and all(ord(c) >= 32 and c != '"' for c in path),
            'Gameplay output requires a bounded ASCII absolute JSON path.')
    lines = ['sed-engine-play-v1', f"{play['moverThing']} {play['cog']} {play['goalBin']} {play['symbol']}",
             str(len(play['steps']))]
    for step in play['steps']:
        lines.append(' '.join(format(step[key],'.17g') for key in
                     ('durationMs','forward','strafe','turn','pitch','activate')))
    return '\n'.join([*lines,path,''])


def validate_telemetry(path, play):
    from invoke_engine_capture import (require, is_link, load_json_bytes, sha256,
                                       integer, number, exact_keys)
    require(path.is_file() and not is_link(path) and path.stat().st_size <= 16*1024*1024,
            'Gameplay telemetry missing, unsafe or over16MiB.')
    raw = path.read_bytes()
    require(len(raw)<=16*1024*1024,'Gameplay telemetry grew beyond16MiB.')
    trace = load_json_bytes(raw)
    required = {'schemaVersion','mode','deterministicSimulation','durationClock','moverIndex',
                'cogIndex','goalBin','symbol','stepCount','samples','events','completed','samplingMaxGapMs'}
    exact_keys(trace,required,set(),'gameplay telemetry')
    expected = {'schemaVersion':1,'mode':'hidden-native-input-play','deterministicSimulation':False,
                'durationClock':'wall-milliseconds','moverIndex':play['moverThing'],'cogIndex':play['cog'],
                'goalBin':play['goalBin'],'symbol':play['symbol'],'stepCount':len(play['steps']),'completed':True}
    for key,value in expected.items():
        require(trace[key] == value and type(trace[key]) is type(value), f'Gameplay telemetry mismatch: {key}')
    samples,events=trace['samples'],trace['events']
    require(isinstance(samples,list) and isinstance(events,list) and 1 < len(samples)
            and len(samples)+len(events) <= 10000,'Invalid gameplay record count.')
    integer(trace['samplingMaxGapMs'],0,90000,'samplingMaxGapMs')
    previous=-1
    previous_game=-1
    completions=[]
    max_gap=0
    active_step=0
    step_start=0
    player_index=None
    for i,s in enumerate(samples):
        exact_keys(s,{'phase','step','wallElapsedMs','stepElapsedMs','gameTimeMs','input','controlReads',
                      'player','mover','cog','goalValue'},set(),'sample')
        require(s['phase'] in ('initial','sample','step-complete'),'Unknown sample phase.')
        wall=integer(s['wallElapsedMs'],0,90000,'wallElapsedMs')
        require(wall >= previous,'Gameplay samples are not monotonic.')
        if previous >= 0: max_gap=max(max_gap,wall-previous)
        previous=wall
        game=integer(s['gameTimeMs'],0,2**32-1,'gameTimeMs')
        require(game >= previous_game,'Gameplay sample game clock regressed or wrapped during the bounded run.')
        previous_game=game
        integer(s['stepElapsedMs'],0,90000,'stepElapsedMs')
        step=integer(s['step'],-1,len(play['steps'])-1,'sample step')
        if i == 0:
            require(s['phase']=='initial' and step==-1,'Telemetry must begin with an uninjected initial sample.')
            require(wall==0 and s['stepElapsedMs']==0,'Initial sample must establish elapsed time zero.')
            require(s['input']==dict(durationMs=0,forward=0,strafe=0,turn=0,pitch=0,activate=0)
                    and all(v==0 for v in s['controlReads'].values()),'Initial sample must precede injected controls.')
        else:
            require(s['phase']!='initial' and step>=0,'Invalid repeated initial sample.')
            require(step==active_step,'Active gameplay step skipped, regressed or continued after completion.')
            require(s['stepElapsedMs']==wall-step_start,'Step elapsed time disagrees with observed step boundaries.')
            require(s['input']==play['steps'][step],'Recorded input differs from reviewed schedule.')
            duration=play['steps'][step]['durationMs']
            require((s['stepElapsedMs']>=duration) == (s['phase']=='step-complete'),
                    'Gameplay step completion disagrees with requested duration.')
        if s['phase']=='step-complete':
            completions.append(step)
            active_step+=1
            step_start=wall
        p,m,c=s['player'],s['mover'],s['cog']
        exact_keys(p,{'index','sector','position','velocity','forward','headPitchYawRoll','health'},set(),'player')
        exact_keys(m,{'sector','position','currentFrame','goalFrame','pathFlags'},set(),'mover')
        exact_keys(c,{'valueType','value'},set(),'cog')
        for entity in (p,m):
            integer(entity['sector'],-1,1000000,'observed sector')
        integer(p['index'],0,1000000,'observed player')
        if player_index is None: player_index=p['index']
        require(p['index']==player_index,'Observed player identity changed during the run.')
        for vector in (p['position'],p['velocity'],p['forward'],p['headPitchYawRoll'],m['position']):
            require(isinstance(vector,list) and len(vector)==3,'Invalid observed vector.')
            for v in vector: number(v,-1000000,1000000,'observed vector component')
        number(p['health'],0,1000000,'health')
        for field in ('currentFrame','goalFrame'): integer(m[field],-1,1000000,field)
        integer(m['pathFlags'],0,2**32-1,'pathFlags')
        require(type(c['valueType']) is int and c['valueType'] in (2,3),'Observed COG symbol is not numeric.')
        number(c['value'],-1e12,1e12,'cog value')
        number(s['goalValue'],-1e12,1e12,'goal value')
        exact_keys(s['controlReads'],{'forward','strafe','turn','pitch','activationEdges'},set(),'controlReads')
        for v in s['controlReads'].values(): integer(v,0,2**31-1,'control read count')
    require(completions==list(range(len(play['steps']))),'Missing, duplicate or reordered step completion.')
    require(trace['samplingMaxGapMs'] >= max_gap,'Reported sample-gap bound understates observations.')
    previous=-1
    previous_game=samples[0]['gameTimeMs']
    for e in events:
        require(isinstance(e,dict),'Gameplay event must be an object.')
        kind=e.get('kind')
        fields={'targetType','targetIndex','cogIndex','message','senderType','senderIndex','sourceType','sourceIndex','linkId'}
        require(kind in ('activation-hit','cog-dispatch'),'Unknown gameplay event kind.')
        exact_keys(e,{'kind','wallElapsedMs','gameTimeMs'}|fields,set(),'gameplay event')
        wall=integer(e['wallElapsedMs'],0,samples[-1]['wallElapsedMs'],'event wallElapsedMs')
        require(wall >= previous,'Gameplay events are not monotonic.')
        previous=wall
        for key in fields: integer(e[key],-1,2**31-1,key)
        game=integer(e['gameTimeMs'],0,samples[-1]['gameTimeMs'],'event gameTimeMs')
        require(game >= previous_game,'Gameplay event game clock regressed or lies before the initial sample.')
        previous_game=game
    return {'telemetrySha256':hashlib.sha256(raw).hexdigest(),'observed':trace,
            'claim':'Raw native state/events, not an engine-issued success verdict.'}
