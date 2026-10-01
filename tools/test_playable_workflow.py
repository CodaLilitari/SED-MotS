#!/usr/bin/env python3
"""Execute the original PLAY-01 exercise in isolated hidden OpenMotS processes.

Raw telemetry is produced by the native observer. This separate evaluator decides
whether observed movement, activation, path arrival and goal changes agree.
Retail assets must come from an explicit local manifest, never this repository.
"""
from __future__ import annotations
import argparse
import json
import math
import hashlib
from pathlib import Path

from invoke_engine_capture import run_capture, sha256, write_json, require, load_json_bytes


def step(ms, forward=0, activate=0):
    return {'durationMs':ms,'forward':forward,'strafe':0,'turn':0,'pitch':0,'activate':activate}


def schedule(activate=True):
    return [step(400),step(1100,1),step(100,activate=int(activate)),step(1900),
            step(900,1),step(1500,1),step(1500,1),step(400)]


def evaluate(trace):
    """Fixture-specific offline oracle. Intent fields never count as consequence."""
    samples=trace['samples']; events=trace['events']
    first=samples[0]; final=samples[-1]
    ended={s['step']:s for s in samples if s['phase']=='step-complete'}
    def terminal_open(sample):
        mover=sample['mover']
        # Native path arrival clears the movement bits (0x17); unrelated bits
        # are not treated as movement. The goal frame resets on native arrival.
        return (mover['currentFrame']==1 and mover['pathFlags'] & 0x17 == 0
                and math.dist(mover['position'],[0,.499,1.4])<.005)
    checks={}
    checks['initialClosedIncomplete']=(first['player']['index']==1 and first['player']['sector']==0
        and abs(first['player']['position'][1]-.30)<.02
        and math.dist(first['mover']['position'],[0,.499,.3])<.005
        and first['mover']['currentFrame']==0 and first['cog']['value']==0
        and first['goalValue']==1)
    checks['postInitializationIncomplete']=(ended[0]['goalValue']==1 and ended[0]['cog']['value']==0)
    blocked=ended[1]
    checks['closedForwardAttemptBlocked']=(blocked['controlReads']['forward']>0
        and blocked['player']['sector']==0 and .32<blocked['player']['position'][1]<.45
        and math.dist(blocked['mover']['position'],[0,.499,.3])<.005
        and blocked['goalValue']==1 and blocked['cog']['value']==0)
    checks['scheduledControlsConsumed']=(all(ended[index]['controlReads']['forward']>0 for index in (1,4,5,6))
        and ended[2]['controlReads']['activationEdges']==1
        and all(s['controlReads']['activationEdges'] in (0,1)
                and (s['step']==2 or s['controlReads']['activationEdges']==0) for s in samples))
    hits=[e for e in events if e['kind']=='activation-hit' and e['targetType']==1
          and e['targetIndex']==0 and e['sourceIndex']==first['player']['index']]
    dispatch=[e for e in events if e['kind']=='cog-dispatch' and e['cogIndex']==0]
    activations=[e for e in dispatch if e['message']==1 and e['senderIndex']==0
                 and e['senderType']==3 and e['sourceType']==3
                 and e['sourceIndex']==first['player']['index'] and e['linkId']==1]
    checks['actualActivationTargetAndDispatch']=(len(hits)==1 and len(activations)==1
        and hits[0]['wallElapsedMs']<=activations[0]['wallElapsedMs']
        and blocked['wallElapsedMs']<=hits[0]['wallElapsedMs']<=activations[0]['wallElapsedMs']<=ended[2]['wallElapsedMs']
        and ended[2]['controlReads']['activationEdges']==1
        and events.index(hits[0])<events.index(activations[0]))
    open_state=ended[3]
    arrivals=[e for e in dispatch if e['message']==11 and e['senderIndex']==0
              and e['senderType']==3 and e['sourceType']==0 and e['sourceIndex']==-1 and e['linkId']==1]
    checks['realPathMotionAndArrival']=(any(.31<s['mover']['position'][2]<1.39 for s in samples)
        and math.dist(open_state['mover']['position'],[0,.499,1.4])<.005
        and open_state['mover']['currentFrame']==1 and open_state['cog']['value']==2
        and open_state['goalValue']==1 and len(arrivals)==1 and len(activations)==1
        and activations[0]['wallElapsedMs']<=arrivals[0]['wallElapsedMs']<=open_state['wallElapsedMs']
        and events.index(activations[0])<events.index(arrivals[0])
        and all(math.dist(s['mover']['position'],[0,.499,.3])<.005 for s in samples
                if s['wallElapsedMs']<activations[0]['wallElapsedMs']))
    path_valid=True
    for a,b in zip(samples,samples[1:]):
        pa,pb=a['mover']['position'],b['mover']['position']
        # The fixture's motion budget is tied to observed wall time. A jump in
        # the independently reported simulation clock cannot excuse a teleport.
        dt=(b['wallElapsedMs']-a['wallElapsedMs'])/1000
        if (abs(pb[0])>.005 or abs(pb[1]-.499)>.005 or not .295<=pb[2]<=1.405
                or pb[2]+.005<pa[2] or math.dist(pa,pb)>.9*dt+.015): path_valid=False
    checks['continuousAuthoredMoverPath']=(path_valid and len(arrivals)==1 and len(activations)==1
        and any(.31<s['mover']['position'][2]<1.39
                and activations[0]['wallElapsedMs']<=s['wallElapsedMs']<=arrivals[0]['wallElapsedMs'] for s in samples)
        and all(math.dist(s['mover']['position'],[0,.499,1.4])<.005 for s in samples
                if s['wallElapsedMs']>=arrivals[0]['wallElapsedMs']))
    sectors=[]
    for s in samples:
        if not sectors or sectors[-1]!=s['player']['sector']: sectors.append(s['player']['sector'])
    checks['uniqueSectorSequence']=sectors==[0,1,2,3]
    crossings=[]
    continuous=True
    for a,b in zip(samples,samples[1:]):
        pa,pb=a['player']['position'],b['player']['position']
        dt=(b['wallElapsedMs']-a['wallElapsedMs'])/1000
        if math.dist(pa,pb)>3*dt+.03: continuous=False
        if a['player']['sector']!=b['player']['sector']:
            target=b['player']['sector']; plane=target-.5
            valid=(target==a['player']['sector']+1 and pa[1]<=plane<=pb[1] and pb[1]>pa[1])
            if valid:
                t=(plane-pa[1])/(pb[1]-pa[1]); x=pa[0]+t*(pb[0]-pa[0]); z=pa[2]+t*(pb[2]-pa[2])
                valid=abs(x)<.5 and -.2<=z<=.8
            crossings.append({'from':a['player']['sector'],'to':target,'valid':valid,
                              'beforeMs':a['wallElapsedMs'],'afterMs':b['wallElapsedMs']})
    checks['continuousIntendedPortalCrossings']=(continuous and trace['samplingMaxGapMs']<=200
        and len(crossings)==3 and all(c['valid'] for c in crossings))
    terminal_witnesses=([s for s in samples if s['wallElapsedMs']>=arrivals[0]['wallElapsedMs']
                         and s['cog']['value']==2 and terminal_open(s)] if len(arrivals)==1 else [])
    checks['firstGateCrossingAfterOpen']=(len(arrivals)==1 and bool(terminal_witnesses)
        and bool(crossings) and crossings[0]['from']==0 and crossings[0]['to']==1
        # A crossing happens somewhere inside its observation interval. Require
        # the entire interval after both witnesses, not just its later endpoint.
        and crossings[0]['beforeMs']>=arrivals[0]['wallElapsedMs']
        and crossings[0]['beforeMs']>=terminal_witnesses[0]['wallElapsedMs']
        and all(s['player']['sector']==0 and s['player']['position'][1]<.5
                for s in samples if s['wallElapsedMs']<arrivals[0]['wallElapsedMs']))
    # The authored startup ORs bit 1 into goal 0. Objective ENTERED ORs bit 2
    # immediately before assigning phase 3, with no intervening yield; the exit
    # changes goal 1, not this observed bin. Do not truncate a flex value into a
    # plausible flag: 2.5, late completion, or a later regression is not this run.
    goal_transitions=[]
    for index,s in enumerate(samples):
        if not goal_transitions or s['goalValue']!=samples[goal_transitions[-1]]['goalValue']:
            goal_transitions.append(index)
    goal_sequence=[samples[index]['goalValue'] for index in goal_transitions]
    checks['exactObjectiveGoalSequence']=goal_sequence==[1,3]
    completed=[s for s in samples if s['goalValue']==3]
    objective_events=[e for e in dispatch if e['message']==6 and e['senderIndex']==2
                      and e['senderType']==5 and e['sourceType']==3
                      and e['sourceIndex']==first['player']['index'] and e['linkId']==2]
    checks['objectiveTransitionAfterEntry']=(bool(completed) and bool(objective_events)
        and completed[0]['player']['sector']==2 and completed[0]['cog']['value']==3
        and len(arrivals)==1 and events.index(arrivals[0])<events.index(objective_events[0])
        and objective_events[0]['wallElapsedMs']<=completed[0]['wallElapsedMs']
        and len(crossings)>=2 and crossings[1]['beforeMs']<=completed[0]['wallElapsedMs']
        and crossings[1]['beforeMs']<=objective_events[0]['wallElapsedMs']<=crossings[1]['afterMs']
        and all(s['goalValue']==1 for s in samples if
                ended[0]['wallElapsedMs']<=s['wallElapsedMs']<objective_events[0]['wallElapsedMs']))
    exit_events=[e for e in dispatch if e['message']==6 and e['senderIndex']==3
                 and e['senderType']==5 and e['sourceType']==3
                 and e['sourceIndex']==first['player']['index'] and e['linkId']==3]
    checks['exitAfterObjective']=(final['player']['sector']==3 and final['cog']['value']==4
        and final['goalValue']==3 and bool(completed)
        and bool(exit_events) and bool(objective_events)
        and len(crossings)==3 and crossings[2]['beforeMs']<=exit_events[0]['wallElapsedMs']<=crossings[2]['afterMs']
        and events.index(objective_events[0])<events.index(exit_events[0])
        and completed[0]['wallElapsedMs']<=exit_events[0]['wallElapsedMs']<=final['wallElapsedMs'])
    transitions=[]
    for index,s in enumerate(samples):
        if not transitions or s['cog']['value']!=samples[transitions[-1]]['cog']['value']:
            transitions.append(index)
    phase_sequence=[samples[index]['cog']['value'] for index in transitions]
    checks['objectiveGoalAlignedWithPhaseThree']=(goal_sequence==[1,3]
        and phase_sequence==[0,1,2,3,4] and len(objective_events)==1
        and goal_transitions[1]==transitions[3]
        # Compare sample indices as well as inclusive timestamps. Millisecond
        # ties do not excuse observing phase 3 before its goal has completed.
        and samples[goal_transitions[1]-1]['wallElapsedMs']<=objective_events[0]['wallElapsedMs']
            <=samples[goal_transitions[1]]['wallElapsedMs'])
    phase_one=[s for s in samples if s['cog']['value']==1]
    causal_phases=(phase_sequence==[0,1,2,3,4]
        and all(len(group)==1 for group in (activations,arrivals,objective_events,exit_events)))
    if causal_phases:
        transition_events=[activations[0],arrivals[0],objective_events[0],exit_events[0]]
        # Dispatch is logged before handler execution; the first changed sample
        # is after the update. Equal timestamps at millisecond resolution are valid.
        causal_phases=all(samples[index-1]['wallElapsedMs']<=event['wallElapsedMs']<=samples[index]['wallElapsedMs']
                          for index,event in zip(transitions[1:],transition_events))
        causal_phases=(causal_phases and bool(phase_one)
            and all(s['mover']['goalFrame']==1 and s['mover']['pathFlags'] & 0x17 != 0 for s in phase_one)
            and any(.31<s['mover']['position'][2]<1.39 for s in phase_one)
            and terminal_open(samples[transitions[2]]))
    checks['causalPhaseTransitions']=causal_phases
    checks['playerAlive']=all(s['player']['health']>0 for s in samples)
    return {'passed':all(checks.values()),'checks':checks,'sectors':sectors,'crossings':crossings,
            'sampleCount':len(samples),'eventCount':len(events),'samplingMaxGapMs':trace['samplingMaxGapMs'],
            'final':{'player':final['player'],'mover':final['mover'],'phase':final['cog']['value'],
                     'goalValue':final['goalValue']}}


def run(fixture_path, bootstrap_path, engine_path, output, repeats=3, only=None):
    require(1<=repeats<=5,'Use1..5 bounded repeat processes.')
    fixture_raw=fixture_path.read_bytes(); bootstrap_raw=bootstrap_path.read_bytes()
    fixture_hash=hashlib.sha256(fixture_raw).hexdigest(); bootstrap_hash=hashlib.sha256(bootstrap_raw).hexdigest()
    fixture=load_json_bytes(fixture_raw); bootstrap=load_json_bytes(bootstrap_raw)
    require(fixture['status']=='authored','Fixture authoring did not complete.')
    output.mkdir(parents=True,exist_ok=False)
    report={'schemaVersion':1,'status':'running','runs':[],
            'limits':['Real-time input; not deterministic stepping.','No retail runtime or campaign transition claim.',
                      'Runtime resource lookup is not observed; explicit alias scan and staged hashes are checked.']}
    try:
        report.update(fixtureSha256=fixture_hash,bootstrapSha256=bootstrap_hash,
                      engineSha256=sha256(engine_path),oracleSha256=sha256(Path(__file__)))
        require(sha256(fixture_path)==fixture_hash and sha256(bootstrap_path)==bootstrap_hash,
                'Fixture or bootstrap manifest changed after parsing.')
        variants={v['name']:v for v in fixture['variants']}
        files=[dict(item) for item in bootstrap['files']]
        for item in files:
            if item['role']=='engine': item.update(source=str(engine_path),sha256=sha256(engine_path))
        cases=[('positive-'+str(i+1),'playable',True) for i in range(repeats)]+[
            ('no-activation','playable',False),('wrong-objective','wrong-objective',True),
            ('residual-barrier','residual-barrier',True),('precompleted-goal','precompleted-goal',True)]
        if only: cases=[case for case in cases if case[0]==only]; require(cases,'Unknown case.')
        for name,variant,activate in cases:
            request={'schemaVersion':1,'episode':'SEDPLAY','map':'playcheck.jkl',
                'camera':{'width':640,'height':480,'sector':0,'position':[.25,-.4,.35],
                          'pitchYawRoll':[-10,0,0],'horizontalFov':90},
                'frame':3,'engineTimeoutMs':30000,'runnerTimeoutMs':40000,
                'files':files+variants[variant]['files'],
                'gameplay':{'schemaVersion':1,'moverThing':0,'cog':0,'goalBin':100,'symbol':'phase',
                            'steps':schedule(activate)}}
            path=output/(name+'.request.json'); write_json(path,request)
            receipt=run_capture(path,output/name)
            entry={'case':name,'variant':variant,'launcherStatus':receipt['status'],
                   'launcherReceipt':str(output/name/'launcher-receipt.json')}
            report['runs'].append(entry)
            if receipt['status']!='completed':
                entry['error']=receipt.get('error'); raise RuntimeError(f'{name}: {entry["error"]}')
            trace=receipt['gameplay']['observed']; result=evaluate(trace); entry['evaluation']=result
            if name.startswith('positive'):
                require(result['passed'],f'{name} did not meet gameplay criteria: {result["checks"]}')
            else:
                require(not result['passed'],f'Negative control unexpectedly passed: {name}')
                f=result['final']
                if name=='no-activation':
                    require(f['phase']==0 and f['goalValue']==1 and f['player']['sector']==0
                            and f['mover']['currentFrame']==0,'No-activation failed for an unexpected reason.')
                elif name=='wrong-objective':
                    require(f['phase']==2 and f['goalValue']==1 and f['player']['sector']==3
                            and result['checks']['realPathMotionAndArrival'],'Wrong-objective failed before its intended fault.')
                elif name=='residual-barrier':
                    require(f['phase']==2 and f['goalValue']==1 and f['player']['sector'] in (0,1)
                            and result['checks']['realPathMotionAndArrival'],'Residual barrier did not isolate passability.')
                elif name=='precompleted-goal':
                    require(not result['checks']['initialClosedIncomplete'] and trace['samples'][0]['goalValue']==3,
                            'Precompleted goal was not rejected at the initial-state gate.')
            entry['acceptedForCase']=True
        report['status']='passed' if only is None and repeats>=3 else 'diagnostic-passed'
    except BaseException as error:
        report['status']='failed'; report['error']=str(error); raise
    finally:
        try:
            report['manifestsUnchanged']=(sha256(fixture_path)==fixture_hash and sha256(bootstrap_path)==bootstrap_hash)
        except (OSError,ValueError) as error:
            report['manifestsUnchanged']=False
            report['finalIdentityError']=f'{type(error).__name__}: {error}'
        if not report['manifestsUnchanged']:
            report['status']='failed'
            report.setdefault('error','Fixture or bootstrap manifest changed during the run.')
        write_json(output/'acceptance.json',report)
    return report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture',type=Path,required=True)
    parser.add_argument('--bootstrap',type=Path,required=True)
    parser.add_argument('--engine',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--repeats',type=int,default=3)
    parser.add_argument('--only',choices=['positive-1','no-activation','wrong-objective','residual-barrier','precompleted-goal'])
    a=parser.parse_args()
    r=run(a.fixture.resolve(strict=True),a.bootstrap.resolve(strict=True),a.engine.resolve(strict=True),
          a.output.resolve(),a.repeats,a.only)
    print(json.dumps({'status':r['status'],'acceptance':str(a.output/'acceptance.json')}))
    raise SystemExit(0 if r['status'] in ('passed','diagnostic-passed') else 1)
