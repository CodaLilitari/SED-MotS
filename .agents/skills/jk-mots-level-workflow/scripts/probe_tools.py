"""Read exact installed SED versions/schemas without changing levels or launching UI."""
import argparse, hashlib, json, pathlib, subprocess, sys

def run(path, args):
    completed = subprocess.run([str(path),*args],capture_output=True,text=True,encoding='utf-8',timeout=30)
    if completed.returncode:
        raise ValueError(f'{path.name} exited {completed.returncode}: {completed.stderr[:1500]} {completed.stdout[:1500]}')
    return json.loads(completed.stdout)

def probe(root):
    root=pathlib.Path(root).resolve()
    agent=root/'agent/sed-agent.exe'; mcp=root/'mcp/sed-mcp.exe'
    if not agent.is_file(): raise ValueError('No agent/sed-agent.exe under the explicit distribution root')
    result={'sedRoot':str(root),'agent':{'path':str(agent),'sha256':hashlib.sha256(agent.read_bytes()).hexdigest(),'version':run(agent,['--version'])},
            'commands':[{'name':x['name'],'usage':x['usage'],'writesFiles':x['writesFiles']} for x in run(agent,['schema','commands'])['commands']],
            'documentation':{name:(root/'docs'/name).is_file() for name in ['AGENT_PROTOCOL.md','AGENT_WORKFLOWS.md','MCP_INTEGRATION.md']},
            'limits':'Read-only capability probe; no level edit, graphical preview, engine test or MCP connection established'}
    if mcp.is_file():
        result['mcp']={'path':str(mcp),'sha256':hashlib.sha256(mcp.read_bytes()).hexdigest(),'version':run(mcp,['--version'])}
    return result

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--config',type=pathlib.Path); p.add_argument('--sed-root',type=pathlib.Path); p.add_argument('--output',type=pathlib.Path)
    args=p.parse_args()
    config=json.loads(args.config.read_text(encoding='utf-8-sig')) if args.config else {}
    root=args.sed_root or config.get('sedRoot')
    if not root: p.error('Supply --sed-root or --config containing sedRoot')
    result=probe(root)
    if args.output:
        with args.output.open('x',encoding='utf-8') as f: json.dump(result,f,indent=2); f.write('\n')
    print(json.dumps(result,indent=2))

if __name__=='__main__':
    try: main()
    except (ValueError,OSError,subprocess.SubprocessError) as exc:
        print(json.dumps({'error':str(exc)})); sys.exit(2)
