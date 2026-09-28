#!/usr/bin/env python3
"""Author an original MotS gate/objective exercise using actual MCP plan/apply.

The seed supplies original resources, engine bootstrap tables and two templates.
The connected rooms, placed objects, path and COG are created through SED tools.
No engine or GUI is launched. Runtime success is a separate acceptance receipt.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import re
import uuid

from new_engine_lighting_fixture import (BOOTSTRAP_CAPACITIES, colormap, gob,
    json_bytes, material, seed_level, sha, write_new)
from test_mcp_workflow import Client


MISSION = b'''# Original SED PLAY-01 gate and two-stage objective. No retail script copied.
symbols
thing gate linkid=1
sector objective linkid=2
sector exitroom linkid=3
thing player local
int phase=0 local
message startup
message activated
message arrived
message entered
end
code
startup:
    player = GetLocalPlayerThing();
    SetMasterCOG(GetSelfCOG());
    SetInv(player, 99, 2000);
    SetGoalFlags(player, 0, 1);
    SetGoalFlags(player, 1, 1);
    Return;
activated:
    if ((GetSenderID() == 1) && (GetSourceRef() == player) && (phase == 0))
    {
        phase = 1;
        # MotS MoveToFrame scales this argument by0.1: 7 means0.7 world units/s.
        MoveToFrame(gate, 1, 7);
    }
    Return;
arrived:
    if ((GetSenderRef() == gate) && (GetCurFrame(gate) == 1) && (phase == 1))
        phase = 2;
    Return;
entered:
    if (GetSourceRef() != player) Return;
    if ((GetSenderID() == 2) && (phase == 2))
    {
        SetGoalFlags(player, 0, 2);
        phase = 3;
    }
    if ((GetSenderID() == 3) && (phase == 3))
    {
        SetGoalFlags(player, 1, 2);
        phase = 4;
    }
    Return;
end
'''


def cube_model(name, half_size, mat):
    x, y, z = half_size
    vertices = [(-x,-y,-z),(x,-y,-z),(x,y,-z),(-x,y,-z),
                (-x,-y,z),(x,-y,z),(x,y,z),(-x,y,z)]
    # Outward face normals for a solid collision mesh, unlike inward world faces.
    faces = [(3,2,1,0),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)]
    normals = [(0,0,-1),(0,0,1),(0,-1,0),(1,0,0),(0,1,0),(-1,0,0)]
    radius = sum(v*v for v in half_size) ** 0.5
    lines = ['SECTION: HEADER','3DO 2.1','SECTION: MODELRESOURCE','MATERIALS 1',
             '0: '+mat,'SECTION: GEOMETRYDEF',f'RADIUS {radius}','INSERT OFFSET 0 0 0',
             'GEOSETS 1','GEOSET 0','MESHES 1','MESH 0','NAME '+name,f'RADIUS {radius}',
             'GEOMETRYMODE 4','LIGHTINGMODE 3','TEXTUREMODE 3','VERTICES 8']
    lines += [f'{i}: {a} {b} {c} 0.5' for i,(a,b,c) in enumerate(vertices)]
    lines += ['TEXTURE VERTICES 4','0: 0 0','1: 2 0','2: 2 2','3: 0 2','VERTEX NORMALS 8']
    lines += [f'{i}: 0 0 1' for i in range(8)]
    lines += ['FACES 6']
    lines += [f'{i}: 0 0x0 4 3 3 0 4 '+ ' '.join(f'{v},{uv}' for uv,v in enumerate(face))
              for i,face in enumerate(faces)]
    lines += ['FACE NORMALS 6'] + [f'{i}: {a} {b} {c}' for i,(a,b,c) in enumerate(normals)]
    lines += ['SECTION: HIERARCHYDEF','HIERARCHY NODES 1',
              '0: 0x0 0x10 0 -1 -1 -1 0 0 0 0 0 0 0 0 0 0 root']
    return ('\n'.join(lines)+'\n').encode('ascii')


def seed():
    """An explicit original scaffold, not a post-hoc edit of a tool-produced map."""
    text = seed_level().decode('ascii').replace('ENGINE-01','PLAY-01')
    text = text.replace('archroom.mat','playroom.mat').replace('archpanel.mat','playgate.mat')
    text = text.replace('archcheck.cmp','playcheck.cmp')
    text = text.replace('archpanel.3do','playgate.3do').replace('archspawn.3do','playspawn.3do')
    text = text.replace('WORLD GRAVITY 0','WORLD GRAVITY 4')
    vertices = [(-.5,-.5,-.2),(.5,-.5,-.2),(.5,.5,-.2),(-.5,.5,-.2),
                (-.5,-.5,.8),(.5,-.5,.8),(.5,.5,.8),(-.5,.5,.8)]
    table = 'World vertices 8\n' + ''.join(f'{i}: {x} {y} {z}\n' for i,(x,y,z) in enumerate(vertices))
    text = re.sub(r'World vertices 8\n.*?(?=World texture vertices)',table,text,flags=re.S)
    text = text.replace('BOUNDBOX -2 -2 0 2 2 3','BOUNDBOX -.5 -.5 -.2 .5 .5 .8')
    text = text.replace('CENTER 0 0 1.5','CENTER 0 0 .3').replace('RADIUS 4','RADIUS 1')
    text = re.sub(r'SECTION: TEMPLATES\n.*?\nEND', '''SECTION: TEMPLATES
World templates 512
playgate none type=cog move=path collide=3 thingflags=0x8 model3d=playgate.3do size=0.71 movesize=0.71
playspawn none type=player move=physics collide=1 thingflags=0x20000401 model3d=playspawn.3do soundclass=ky.snd size=.065 movesize=.065 mass=150 physflags=0x4a4f maxrotvel=200 maxvel=1 maxrotthrust=180 jumpspeed=1.5 eyeoffset=(0/0/.037) minheadpitch=-80 maxheadpitch=80 surfdrag=3 airdrag=.5 staticdrag=.3 health=100 maxhealth=100 maxthrust=2 typeflags=0x1
END''',text,flags=re.S)
    text = re.sub(r'SECTION: THINGS\n.*?\nEND','SECTION: THINGS\nWorld things 512\nEND',text,flags=re.S)
    text = re.sub(r'SECTION: ARCHLIGHTING\n.*?\nEND','',text,flags=re.S)
    return text.encode('ascii')


def assert_corridor_topology(summary, surfaces, vertices):
    """Prove this fixture's exact four-box chain from complete tool readback.

    This is a fixture assertion, not a general level topology validator. It
    checks actual corner positions as well as reciprocal adjoins, so extra
    rooms, a bypass, a duplicate portal or a narrowed opening cannot substitute
    for the corridor assumed by the runtime crossing oracle.
    """
    assert summary['items'][0]['counts']['sectors'] == 4, 'Expected exactly four sectors.'
    assert summary['items'][0]['counts']['surfaces'] == 24, 'Expected six faces per room.'
    assert len(surfaces) == len(vertices) == 4

    def complete(page, count):
        assert page['offset'] == 0 and page['nextOffset'] is None
        assert page['total'] == page['sourceTotal'] == page['returnedCount'] == len(page['items']) == count
        assert [row['index'] for row in page['items']] == list(range(count))

    def close(a, b):
        return len(a) == len(b) and all(math.isclose(x, y, rel_tol=0, abs_tol=1e-8) for x, y in zip(a, b))

    links = []
    for sector in range(4):
        complete(surfaces[sector], 6)
        complete(vertices[sector], 8)
        points = [v['position'] for v in vertices[sector]['items']]
        expected = [[x, y, z] for x in (-.5, .5) for y in (sector-.5, sector+.5) for z in (-.2, .8)]
        assert all(sum(close(p, e) for p in points) == 1 for e in expected), 'Unexpected room corner.'
        seen_faces = set()
        for face in surfaces[sector]['items']:
            assert face['sector'] == sector
            corners = face['corners']
            assert len(corners) == 4 and len({c['vertex'] for c in corners}) == 4
            assert all(type(c['vertex']) is int and 0 <= c['vertex'] < 8 for c in corners)
            polygon = [points[c['vertex']] for c in corners]
            assert all(sum(not math.isclose(a, b, rel_tol=0, abs_tol=1e-8) for a, b in zip(p, q)) == 1
                       for p, q in zip(polygon, polygon[1:] + polygon[:1])), 'Crossed or degenerate polygon.'
            planes = [(axis, boundary) for axis, bounds in enumerate(((-.5, .5), (sector-.5, sector+.5), (-.2, .8)))
                      for boundary in bounds if all(math.isclose(p[axis], boundary, rel_tol=0, abs_tol=1e-8) for p in polygon)]
            assert len(planes) == 1 and planes[0] not in seen_faces, 'Missing or duplicate box face.'
            axis, boundary = planes[0]
            seen_faces.add(planes[0])
            # All four unique box corners in this plane constitute the complete opening.
            normal = [0, 0, 0]
            normal[axis] = 1 if boundary == (-.5, sector-.5, -.2)[axis] else -1
            assert close(face['normal'], normal), 'Unexpected inward surface normal.'
            neighbor = sector + (1 if boundary > sector else -1) if axis == 1 else -1
            if axis != 1 or not 0 <= neighbor < 4:
                assert (face['adjoinSector'], face['adjoinSurface']) == (-1, -1), 'Unexpected bypass portal.'
                continue
            assert face['adjoinSector'] == neighbor, 'Wrong corridor neighbor.'
            peer_index = face['adjoinSurface']
            assert type(peer_index) is int and 0 <= peer_index < 6
            peer = surfaces[neighbor]['items'][peer_index]
            assert (peer['adjoinSector'], peer['adjoinSurface']) == (sector, face['index']), 'Nonreciprocal portal.'
            links.append((sector, face['index'], neighbor, peer_index))
    assert sorted((a, b) for a, _, b, _ in links) == [(0,1),(1,0),(1,2),(2,1),(2,3),(3,2)]
    return {'sectorCount': 4, 'directedPortals': [list(link) for link in links],
            'surfaces': [page['items'] for page in surfaces], 'vertices': [page['items'] for page in vertices]}


def generate(server: Path, agent: Path, output: Path):
    if not __debug__:
        raise RuntimeError('Authoring assertions require normal Python mode, without -O.')
    output.mkdir(parents=True, exist_ok=False)
    workspace = output / 'project'
    workspace.mkdir()
    entries = {'mat/playroom.mat': material(bytes([250]*4)),
               'mat/playgate.mat': material(bytes((255,64,64,255))),
               'misc/cmp/playcheck.cmp': colormap(),
               '3do/playgate.3do': cube_model('original_gate',(.5,.03,.5),'playgate.mat'),
               '3do/playspawn.3do': cube_model('original_player',(.03,.03,.05),'playroom.mat'),
               'cog/playmission.cog': MISSION,
               'misc/cogstrings.uni': b'MSGS 2\n"GOAL_02000" 0 "Pass the gate and reach the objective room."\n"GOAL_02001" 0 "Reach the exit room."\nEND\n'}
    resources = workspace / 'original-play-resources.gob'
    write_new(resources, gob(entries))
    archives = [{'path': str(resources), 'sha256': sha(resources.read_bytes())}]
    source = workspace / 'seed.jkl'
    write_new(source, seed())
    report = {'schemaVersion':1, 'status':'authoring', 'guiLaunched':False,'gameLaunched':False,
              'generatorSha256':sha(Path(__file__).read_bytes()),
              'server':str(server), 'agent':str(agent), 'serverSha256':sha(server.read_bytes()),
              'agentSha256':sha(agent.read_bytes()), 'resourceArchives':archives,
              'managedAssemblies':[{'path':str(p),'sha256':sha(p.read_bytes())}
                  for folder in (server.parent,agent.parent)
                  for p in (folder/'sed-agent.dll',folder/'sed-mcp.dll',folder/'Sed.Core.dll',folder/'Sed.Formats.dll')
                  if p.is_file()],
              'assetProvenance':'Original generated exercise assets; ky.snd and engine bootstrap remain external.',
              'limits':['The original copyright placeholder requires an OpenMotS QOL build.',
                        'Authoring is exercised over real MCP; this is a scripted client, not a Codex-native MCP session.',
                        'The door is activated directly as an integrated control; no separate wall switch.',
                        'Runtime stages unique loose custom assets; original objective UI strings remain in the authoring archive only, because capture forbids bootstrap-name overrides. Goal inventory is observed; objective-menu text is not tested.',
                        'Exit means the exit-room objective; episode transition is not exercised.'],
              'files':[], 'variants':[], 'calls':[]}
    client = None
    try:
        client = Client(server, agent, workspace)
        for topic in ('addCog','setThingFrames','extrudeSurface','addThing'):
            client.call('sed_discover',{'topic':topic})

        def query(path, collection, **kwargs):
            return client.call('sed_query',{'level':str(path),
                'request':{'schemaVersion':1,'collection':collection,**kwargs}})[0]

        def topology(path):
            return assert_corridor_topology(query(path,'summary'),
                [query(path,'surfaces',sector=i) for i in range(4)],
                [query(path,'vertices',sector=i) for i in range(4)])

        def apply(path, name, operations, with_resources=False, resource_set=None):
            current = query(path,'summary')
            request = {'schemaVersion':1,'inputSha256':current['inputSha256'],
                       'sourceRevision':current['sourceRevision'],'operations':operations}
            if with_resources: request['resourceArchives'] = resource_set or archives
            plan,_ = client.call('sed_plan',{'level':str(path),'request':request})
            if with_resources: request['resourceRevision'] = plan['resourceRevision']
            target = workspace / (name+'.jkl')
            result,_ = client.call('sed_apply',{'level':str(path),'request':request,
                'output':str(target),'expectedOutputSha256':plan['outputSha256'],'operationId':str(uuid.uuid4())})
            assert sha(target.read_bytes()) == plan['outputSha256']
            assert sha(path.read_bytes()) == current['inputSha256']
            assert result['outcome']['matchesExpectedOutput']
            report['files'].append({'path':str(target),'sha256':sha(target.read_bytes())})
            return target

        for sector in range(3):
            selected,_ = client.call('sed_select',{'level':str(source),'request':{
                'schemaVersion':1,'kind':'surface','sectorPoint':[0,sector,0.3],'outward':[0,1,0]}})
            assert selected['status'] == 'unique'
            source = apply(source,f'rooms-{sector+2}',[
                {'op':'extrudeSurface','target':selected['selection'],'distance':1}])
        geometry = []
        collision = []
        for sector in range(4):
            page = query(source,'surfaces',sector=sector)
            geometry.append(page)
            for surface in page['items']:
                if surface['adjoinSector'] < 0:
                    collision.append({'op':'setSurfaceFlags','sector':sector,'surface':surface['index'],
                                      'flags':5 if surface['normal'][2] > .99 else 4})
        report['geometry'] = geometry
        source = apply(source,'collision-world',collision)
        source = apply(source,'placed',[
            {'op':'addThing','template':'playgate','sector':0,'name':'Gate_Control','position':[0,.499,.3]},
            {'op':'addThing','template':'playspawn','sector':0,'name':'Player','position':[0,.30,0]},
            {'op':'setThingFrames','thing':0,'frames':[
                {'position':[0,.499,.3],'orientation':[0,0,0]},
                {'position':[0,.499,1.4],'orientation':[0,0,0]}]},
            {'op':'addCog','script':'playmission.cog','scriptSha256':sha(MISSION),'bindings':[
                {'symbol':'gate','kind':'thing','targetIndex':0},
                {'symbol':'objective','kind':'sector','targetIndex':2},
                {'symbol':'exitroom','kind':'sector','targetIndex':3}]}],True)
        # The binding-negative control differs only in one reviewed reference.
        wrong = apply(source,'wrong-objective',[
            {'op':'rebindCogReference','cog':0,'parameter':1,'symbol':'objective','kind':'sector',
             'targetIndex':0,'scriptSha256':sha(MISSION)}],True)
        blocked = apply(source,'residual-barrier',[
            {'op':'addThing','template':'playgate','sector':1,'name':'Residual_Barrier','position':[0,.56,.3]}])
        completed_script = MISSION.replace(b'SetGoalFlags(player, 0, 1);',b'SetGoalFlags(player, 0, 3);')
        completed_entries = entries | {'cog/playmission.cog':completed_script}
        completed_resources = workspace/'precompleted-resources.gob'
        write_new(completed_resources,gob(completed_entries))
        completed_archives = [{'path':str(completed_resources),'sha256':sha(completed_resources.read_bytes())}]
        completed = apply(source,'precompleted-goal',[
            {'op':'rebindCogReference','cog':0,'parameter':1,'symbol':'objective','kind':'sector',
             'targetIndex':2,'scriptSha256':sha(completed_script)}],True,completed_archives)
        variants = [('playable',source,archives,entries),('wrong-objective',wrong,archives,entries),
                    ('residual-barrier',blocked,archives,entries),
                    ('precompleted-goal',completed,completed_archives,completed_entries)]
        for name,path,current_archives,current_entries in variants:
            converted_plan,_ = client.call('sed_convert',{'level':str(path)})
            reopened=workspace/(name+'-reopened.jkl')
            client.call('sed_convert',{'level':str(path),'output':str(reopened),
                'operationId':str(uuid.uuid4()),'expectedOutputSha256':converted_plan['outputSha256'],
                'expectedInputSha256':converted_plan['inputSha256'],
                'expectedSourceRevision':converted_plan['sourceRevision']})
            things,cogs=query(path,'things'),query(path,'cogs')
            assert query(reopened,'things')['items']==things['items']
            assert query(reopened,'cogs')['items']==cogs['items']
            corridor = topology(path)
            assert topology(reopened) == corridor, 'Room topology changed on save/reopen.'
            assert len(things['items'])==(3 if name=='residual-barrier' else 2)
            gate,player=things['items'][:2]
            assert (gate['index'],gate['template'],gate['position'],gate['sector'])==(0,'playgate',[0,.499,.3],0)
            assert (player['index'],player['template'],player['position'],player['sector'])==(1,'playspawn',[0,.30,0],0)
            assert gate['sourceParameters']==[
                {'key':'numframes','value':'2'},
                {'key':'frame','value':'(0/0.499/0.3:0/0/0)'},
                {'key':'frame','value':'(0/0.499/1.4:0/0/0)'}]
            expected_values=['0','0' if name=='wrong-objective' else '2','3']
            assert cogs['items']==[{'index':0,'name':'playmission.cog','values':expected_values,
                                  'bindingStatus':'unverified-use-bindings-with-explicit-resources'}]
            binding,_ = client.call('sed_diagnose',{'level':str(path),'kind':'bindings',
                'request':{'schemaVersion':1,'inputSha256':sha(path.read_bytes()),'resourceArchives':current_archives,'cog':0}})
            assert binding['schemaComplete'] and binding['parameterCountMatches']
            assert binding['scriptSha256']==sha(current_entries['cog/playmission.cog'])
            assert [(r['symbolName'],r['type'],r['target']['index']) for r in binding['rows']]==[
                ('gate','thing',0),('objective','sector',int(expected_values[1])),('exitroom','sector',3)]
            validated,_ = client.call('sed_diagnose',{'level':str(path),'kind':'validate',
                'request':{'schemaVersion':1,'inputSha256':sha(path.read_bytes()),'resourceArchives':current_archives}})
            assert validated['valid'], validated
            stage = output/name
            stage.mkdir()
            level = stage/'playcheck.jkl'
            write_new(level,path.read_bytes())
            archive = stage/'playassets.gob'
            write_new(archive,gob(current_entries))
            episode = stage/'episode.jk'
            write_new(episode,b'"SED PLAY-01: Gate and objective"\nTYPE 1\nSEQ 1\n0: 1 1 LEVEL playcheck.jkl 0 0 -1 -1\nEND\n')
            staged_assets=[]
            for logical,data in current_entries.items():
                if logical=='misc/cogstrings.uni': continue
                resource=stage/logical
                write_new(resource,data)
                staged_assets.append((resource,'episode/SEDPLAY/'+logical))
            report['variants'].append({'name':name,'level':str(level),'levelSha256':sha(level.read_bytes()),
                'files':[{'source':str(p),'destination':dest,'sha256':sha(p.read_bytes()),'role':'scenario'}
                    for p,dest in [(episode,'episode/SEDPLAY/episode.jk'),
                                   (level,'episode/SEDPLAY/jkl/playcheck.jkl'),*staged_assets]],
                'authoringArchive':{'path':str(archive),'sha256':sha(archive.read_bytes())},
                'things':things,'cogs':cogs,'binding':binding,
                'topology':corridor,'reopenedTopologyMatches':True,
                'reopenedSha256':sha(reopened.read_bytes()),
                'originalEntries':[{'name':n,'sha256':sha(data),'bytes':len(data)} for n,data in current_entries.items()]})
        report['status'] = 'authored'
    except BaseException as error:
        report['status'] = 'failed'
        report['error'] = str(error)
        raise
    finally:
        if client:
            report['calls'] = client.calls
            client.close()
        write_new(output/'authoring-receipt.json',json_bytes(report))
    return report


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server',type=Path,required=True)
    parser.add_argument('--agent',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    result=generate(args.server.resolve(strict=True),args.agent.resolve(strict=True),args.output.resolve())
    print(json.dumps({'status':result['status'],'receipt':str(args.output/'authoring-receipt.json')}))
