"""Independent synthetic PLAY-01 evidence; no engine or retail assets are run.

The reference trace is constructed from a small kinematic corridor model, not
copied from runtime output or assembled by the production oracle. Mutations
challenge whether observation order establishes the claimed causal chain.
"""
import copy
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import engine_play_contract as contract
import invoke_engine_capture as runner
import test_playable_workflow as workflow
import test_invoke_engine_capture as capture_fixtures


def independent_program():
    durations = (400, 1100, 100, 1900, 900, 1500, 1500, 400)
    return {'schemaVersion': 1, 'moverThing': 0, 'cog': 0,
            'goalBin': 100, 'symbol': 'phase', 'steps': [
                {'durationMs': ms, 'forward': int(i in (1, 4, 5, 6)),
                 'strafe': 0, 'turn': 0, 'pitch': 0, 'activate': int(i == 2)}
                for i, ms in enumerate(durations)]}


def observed_event(kind, when, **fields):
    event = {'kind': kind, 'wallElapsedMs': when, 'gameTimeMs': 10000 + when,
             'targetType': -1, 'targetIndex': -1, 'cogIndex': -1, 'message': -1,
             'senderType': -1, 'senderIndex': -1, 'sourceType': -1,
             'sourceIndex': -1, 'linkId': -1}
    event.update(fields)
    return event


def independent_trace():
    """A blocked approach, one physical activation, then continuous traversal.

    Four room intervals have portals at y=.5,1.5,2.5. Walking after the
    independently timed door arrival is .7 units/s; observations every50ms.
    """
    program = independent_program()
    ends = (400, 1500, 1600, 3500, 4400, 5900, 7400, 7800)
    samples = []
    for t in range(0, 7801, 50):
        index = -1 if t == 0 else next(i for i, end in enumerate(ends) if t <= end)
        elapsed = 0 if index < 0 else t - (ends[index - 1] if index else 0)
        y = .30 + .08 * max(0, min(1, (t - 400) / 1100))
        if t > 3500:
            y = .38 + .0007 * (min(t, 7400) - 3500)
        sector = sum(y >= boundary for boundary in (.5, 1.5, 2.5))
        z = .3 + 1.1 * max(0, min(1, (t - 1550) / 1650))
        phase = 0 if t < 1550 else 1 if t < 3200 else 2 if sector < 2 else 3 if sector == 2 else 4
        command = ({'durationMs': 0, 'forward': 0, 'strafe': 0, 'turn': 0,
                    'pitch': 0, 'activate': 0} if index < 0 else copy.deepcopy(program['steps'][index]))
        samples.append({
            'phase': 'initial' if index < 0 else 'step-complete' if t == ends[index] else 'sample',
            'step': index, 'wallElapsedMs': t, 'stepElapsedMs': elapsed,
            'gameTimeMs': 10000 + t, 'input': command,
            'controlReads': {'forward': elapsed // 25, 'strafe': elapsed // 25,
                             'turn': elapsed // 25, 'pitch': elapsed // 25,
                             'activationEdges': int(index == 2 and t >= 1550)},
            'player': {'index': 1, 'sector': sector, 'position': [0, y, 0],
                       'velocity': [0, .7 if 3500 < t <= 7400 else 0, 0],
                       'forward': [0, 1, 0], 'headPitchYawRoll': [0, 0, 0], 'health': 100},
            'mover': {'sector': 0, 'position': [0, .499, z], 'currentFrame': int(t >= 3200),
                      'goalFrame': int(t >= 1550), 'pathFlags': int(1550 <= t < 3200)},
            'cog': {'valueType': 2, 'value': phase},
            'goalValue': 0 if t == 0 else 1 if sector < 2 else 3})
    objective_ms = next(s['wallElapsedMs'] for s in samples if s['player']['sector'] == 2)
    exit_ms = next(s['wallElapsedMs'] for s in samples if s['player']['sector'] == 3)
    events = [
        observed_event('activation-hit', 1550, targetType=1, targetIndex=0, sourceIndex=1),
        observed_event('cog-dispatch', 1550, cogIndex=0, message=1,
                       senderType=3, senderIndex=0, sourceType=3, sourceIndex=1, linkId=1),
        observed_event('cog-dispatch', 3200, cogIndex=0, message=11,
                       senderType=3, senderIndex=0, sourceType=0, sourceIndex=-1, linkId=1),
        observed_event('cog-dispatch', objective_ms, cogIndex=0, message=6,
                       senderType=5, senderIndex=2, sourceType=3, sourceIndex=1, linkId=2),
        observed_event('cog-dispatch', exit_ms, cogIndex=0, message=6,
                       senderType=5, senderIndex=3, sourceType=3, sourceIndex=1, linkId=3),
    ]
    return {'schemaVersion': 1, 'mode': 'hidden-native-input-play',
            'deterministicSimulation': False, 'durationClock': 'wall-milliseconds',
            'moverIndex': 0, 'cogIndex': 0, 'goalBin': 100, 'symbol': 'phase',
            'stepCount': 8, 'samples': samples, 'events': events,
            'completed': True, 'samplingMaxGapMs': 50}


def clock_inflated_teleport(retain_offset):
    trace = independent_trace()
    for s in trace['samples']:
        t = s['wallElapsedMs']
        if t == 1600:
            s['mover']['position'][2] = 1.35
            s['gameTimeMs'] += 1000000
        elif t > 1600:
            s['mover']['position'][2] = 1.4
            if retain_offset: s['gameTimeMs'] += 1000000
    if retain_offset:
        for e in trace['events']:
            if e['wallElapsedMs'] >= 1600: e['gameTimeMs'] += 1000000
    return trace


class RequestContractTests(unittest.TestCase):
    def test_bounded_program_and_text_preserve_nontrivial_axis_precision(self):
        program = independent_program()
        program['steps'][0]['forward'] = .1234567891234567
        normalized = contract.validate_gameplay(program, 10000)
        with tempfile.TemporaryDirectory() as temp:
            text = contract.gameplay_text(normalized, Path(temp) / 'observed.json')
        self.assertEqual(float(text.splitlines()[3].split()[1]), program['steps'][0]['forward'])
        self.assertTrue(text.startswith('sed-engine-play-v1\n0 0 100 phase\n8\n'))

    def test_invalid_types_numbers_bounds_and_unreviewed_fields_refused(self):
        cases = [('moverThing', True), ('cog', -1), ('goalBin', 116),
                 ('symbol', 'phase;write'), ('symbol', 'a' * 64), ('steps', [])]
        for field, value in cases:
            with self.subTest(field=field, value=value):
                program = independent_program(); program[field] = value
                with self.assertRaises(runner.CaptureError): contract.validate_gameplay(program, 10000)
        for field, value in [('durationMs', 49), ('durationMs', 5001), ('activate', True),
                             ('activate', 2), ('forward', float('nan')), ('pitch', 1.01),
                             ('strafe', float('inf')), ('turn', '0'), ('teleport', 1)]:
            with self.subTest(field=field, value=value):
                program = independent_program(); program['steps'][0][field] = value
                with self.assertRaises(runner.CaptureError): contract.validate_gameplay(program, 10000)
        with self.assertRaises(runner.CaptureError): contract.validate_gameplay(independent_program(), 9799)
        program = independent_program(); program['steps'] *= 5
        with self.assertRaises(runner.CaptureError): contract.validate_gameplay(program, 90000)


class TelemetryContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'trace.json'

    def validate(self, trace):
        self.path.write_text(json.dumps(trace), encoding='utf-8')
        return contract.validate_telemetry(self.path, independent_program())

    def test_independent_valid_trace_is_accepted_and_hashed(self):
        trace = independent_trace()
        result = self.validate(trace)
        self.assertEqual(result['observed'], trace)
        self.assertEqual(result['telemetrySha256'], hashlib.sha256(self.path.read_bytes()).hexdigest())

    def test_duplicate_json_keys_and_nonfinite_state_refused(self):
        self.path.write_text('{"samples":[],"samples":[]}', encoding='utf-8')
        with self.assertRaises(runner.CaptureError): contract.validate_telemetry(self.path, independent_program())
        trace = independent_trace(); trace['samples'][1]['player']['health'] = float('nan')
        with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_missing_or_duplicate_completion_refused(self):
        for duplicate in (False, True):
            with self.subTest(duplicate=duplicate):
                trace = independent_trace()
                at = next(i for i, s in enumerate(trace['samples']) if s['phase'] == 'step-complete')
                if duplicate: trace['samples'].insert(at, copy.deepcopy(trace['samples'][at]))
                else: trace['samples'][at]['phase'] = 'sample'
                with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_early_completion_cannot_be_certified_by_labels(self):
        trace = independent_trace()
        for s in trace['samples']:
            s['wallElapsedMs'] //= 10; s['stepElapsedMs'] //= 10
        for e in trace['events']: e['wallElapsedMs'] //= 10
        trace['samplingMaxGapMs'] = 5
        with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_ordinary_samples_cannot_return_to_completed_steps(self):
        trace = independent_trace()
        s = next(s for s in trace['samples'] if s['wallElapsedMs'] == 1700)
        s['step'] = 0; s['input'] = independent_program()['steps'][0]
        with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_step_elapsed_must_agree_with_observed_boundaries(self):
        trace = independent_trace(); trace['samples'][2]['stepElapsedMs'] = 1
        with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_initial_sample_has_zero_input_and_control_reads(self):
        for key in ('input', 'controlReads'):
            with self.subTest(key=key):
                trace = independent_trace(); trace['samples'][0][key]['forward'] = 1
                with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_player_identity_cannot_change_mid_trace(self):
        trace = independent_trace(); trace['samples'][-1]['player']['index'] = 2
        with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_event_after_final_sample_is_not_observed_session_evidence(self):
        trace = independent_trace(); trace['events'][-1]['wallElapsedMs'] = 7801
        with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_event_shape_errors_use_contract_error(self):
        trace = independent_trace(); trace['events'][0] = None
        with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_sample_game_clock_spike_followed_by_regression_refused(self):
        with self.assertRaisesRegex(runner.CaptureError, 'sample game clock regressed'):
            self.validate(clock_inflated_teleport(False))

    def test_event_game_clock_must_be_ordered_within_sample_interval(self):
        for index, time in ((1, 10000), (0, 9999), (4, 999999)):
            with self.subTest(index=index, time=time):
                trace = independent_trace(); trace['events'][index]['gameTimeMs'] = time
                with self.assertRaises(runner.CaptureError): self.validate(trace)

    def test_monotonic_simulation_clock_jump_is_not_physical_motion_evidence(self):
        # Monotonicity alone cannot close this false-success path. The raw
        # observation schema may accept an offset; the fixture oracle must not
        # use that offset to inflate its wall-clock physical-motion allowance.
        trace = clock_inflated_teleport(True)
        observed = self.validate(trace)['observed']
        self.assertFalse(workflow.evaluate(observed)['passed'])

    def test_digest_identifies_parsed_bytes_if_file_replaced_during_parse(self):
        trace = independent_trace()
        raw = json.dumps(trace).encode('utf-8'); self.path.write_bytes(raw)
        loader = runner.load_json_bytes
        def replace_after_read(data):
            parsed = loader(data)
            self.path.write_bytes(b'{"unrelated":"replacement"}')
            return parsed
        with patch.object(runner, 'load_json_bytes', side_effect=replace_after_read):
            result = contract.validate_telemetry(self.path, independent_program())
        self.assertEqual(result['telemetrySha256'], hashlib.sha256(raw).hexdigest())


class CausalOracleTests(unittest.TestCase):
    def test_complete_independent_chain_passes(self):
        result = workflow.evaluate(independent_trace())
        self.assertTrue(result['passed'], result['checks'])

    def assert_rejected(self, trace):
        result = workflow.evaluate(trace)
        self.assertFalse(result['passed'], result['checks'])

    def test_activation_intent_without_physical_hit_is_insufficient(self):
        trace = independent_trace(); trace['events'] = trace['events'][1:]
        self.assert_rejected(trace)

    def test_wrong_physical_activation_target_rejected(self):
        trace = independent_trace(); trace['events'][0]['targetIndex'] = 9
        self.assert_rejected(trace)

    def test_activation_recorded_after_opening_does_not_cause_motion(self):
        trace = independent_trace()
        for event in trace['events'][:2]: event['wallElapsedMs'] = 3400
        trace['events'].sort(key=lambda e: e['wallElapsedMs'])
        self.assert_rejected(trace)

    def test_activation_during_idle_cannot_be_attributed_to_finished_input_step(self):
        trace = independent_trace()
        for event in trace['events'][:2]: event['wallElapsedMs'] = 1700
        for s in trace['samples']:
            t = s['wallElapsedMs']
            s['mover']['position'][2] = .3 + 1.1 * max(0, min(1, (t - 1700) / 1500))
            if 1550 <= t < 1700:
                s['cog']['value'] = 0; s['mover']['goalFrame'] = 0; s['mover']['pathFlags'] = 0
        self.assert_rejected(trace)

    def test_arrival_before_activation_does_not_certify_this_path(self):
        trace = independent_trace(); trace['events'][2]['wallElapsedMs'] = 1400
        trace['events'].sort(key=lambda e: e['wallElapsedMs'])
        self.assert_rejected(trace)

    def test_dispatch_requires_correct_entity_types_and_link(self):
        for event_index, field, wrong in ((1, 'senderType', 5), (1, 'sourceType', 5),
                                         (2, 'linkId', 99), (2, 'senderType', 5),
                                         (3, 'senderType', 3), (3, 'sourceType', 5)):
            with self.subTest(event=event_index, field=field):
                trace = independent_trace(); trace['events'][event_index][field] = wrong
                self.assert_rejected(trace)

    def test_objective_dispatch_before_actual_entry_is_insufficient(self):
        trace = independent_trace(); trace['events'][3]['wallElapsedMs'] = 3500
        self.assert_rejected(trace)

    def test_exit_requires_real_entered_dispatch_not_final_phase_alone(self):
        trace = independent_trace(); trace['events'].pop()
        self.assert_rejected(trace)

    def test_exit_dispatch_must_coincide_with_exit_crossing(self):
        trace = independent_trace(); trace['events'][-1]['wallElapsedMs'] = 5500
        self.assert_rejected(trace)

    def test_teleport_and_postarrival_excursion_are_not_path_motion(self):
        trace = independent_trace()
        for s in trace['samples']:
            t = s['wallElapsedMs']
            if t >= 1600: s['mover']['position'][2] = 1.4
            if t == 3350: s['mover']['position'][2] = .8
        self.assert_rejected(trace)

    def test_mover_cannot_leave_the_authored_vertical_path(self):
        trace = independent_trace()
        for s in trace['samples']:
            if 2000 <= s['wallElapsedMs'] <= 2500: s['mover']['position'][0] = 1
        self.assert_rejected(trace)

    def test_game_clock_cannot_expand_mover_speed_allowance(self):
        for retain_offset in (False, True):
            with self.subTest(retain_offset=retain_offset):
                trace = clock_inflated_teleport(retain_offset)
                result = workflow.evaluate(trace)
                self.assertFalse(result['checks']['continuousAuthoredMoverPath'])
                self.assertFalse(result['passed'])

    def test_sector_labels_do_not_hide_discontinuous_player_motion(self):
        trace = independent_trace()
        s = next(s for s in trace['samples'] if s['player']['sector'] == 2)
        s['player']['position'][0] = 20
        self.assert_rejected(trace)

    def test_precompleted_goal_is_rejected_even_if_later_chain_looks_right(self):
        trace = independent_trace(); trace['samples'][0]['goalValue'] = 3
        self.assert_rejected(trace)


class WorkflowReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.fixture, self.bootstrap, self.engine = [self.root / name for name in
                                                    ('fixture.json', 'bootstrap.json', 'engine.exe')]
        self.fixture.write_text(json.dumps({'status': 'authored', 'variants': [
            {'name': name, 'files': []} for name in
            ('playable', 'wrong-objective', 'residual-barrier', 'precompleted-goal')]}))
        self.bootstrap.write_text('{"files":[]}')
        self.engine.write_bytes(b'Original inert synthetic engine; never executed.')
        self.output = self.root / 'run'

    def test_existing_directory_is_never_overwritten(self):
        self.output.mkdir(); sentinel = self.output / 'acceptance.json'; sentinel.write_bytes(b'keep')
        with patch.object(workflow, 'run_capture', side_effect=AssertionError('launched')):
            with self.assertRaises((FileExistsError, runner.CaptureError)):
                workflow.run(self.fixture, self.bootstrap, self.engine, self.output, only='positive-1')
        self.assertEqual(sentinel.read_bytes(), b'keep')
        self.assertEqual(list(self.output.iterdir()), [sentinel])

    def test_primary_capture_exception_produces_failure_receipt(self):
        with patch.object(workflow, 'run_capture', side_effect=RuntimeError('original capture failure')):
            with self.assertRaisesRegex(RuntimeError, 'original capture failure'):
                workflow.run(self.fixture, self.bootstrap, self.engine, self.output, only='positive-1')
        report = json.loads((self.output / 'acceptance.json').read_text())
        self.assertEqual(report['status'], 'failed')
        self.assertIn('original capture failure', report['error'])

    def test_missing_manifest_during_failure_does_not_erase_original_receipt(self):
        def fail_capture(*args):
            self.bootstrap.unlink()
            raise RuntimeError('original capture failure')
        with patch.object(workflow, 'run_capture', side_effect=fail_capture):
            with self.assertRaisesRegex(RuntimeError, 'original capture failure'):
                workflow.run(self.fixture, self.bootstrap, self.engine, self.output, only='positive-1')
        report = json.loads((self.output / 'acceptance.json').read_text())
        self.assertEqual(report['status'], 'failed')
        self.assertIn('original capture failure', report['error'])

    def test_post_creation_manifest_error_still_leaves_failure_receipt(self):
        self.fixture.write_text('{"status":"authored","variants":null}')
        with self.assertRaises(Exception):
            workflow.run(self.fixture, self.bootstrap, self.engine, self.output, only='positive-1')
        report = json.loads((self.output / 'acceptance.json').read_text())
        self.assertEqual(report['status'], 'failed')

    def test_manifest_digests_identify_parsed_bytes_and_drift_cannot_pass(self):
        for target, digest_key in ((self.fixture, 'fixtureSha256'), (self.bootstrap, 'bootstrapSha256')):
            with self.subTest(target=target.name):
                raw = target.read_bytes(); replaced = False
                loader = workflow.load_json_bytes
                output = self.root / ('drift-' + target.stem)
                def replace_after_read(data):
                    nonlocal replaced
                    parsed = loader(data)
                    if data == raw and not replaced:
                        target.write_text(json.dumps(dict(parsed, changedAfterRead=True)))
                        replaced = True
                    return parsed
                receipt = {'status': 'completed', 'gameplay': {'observed': independent_trace()}}
                with patch.object(workflow, 'load_json_bytes', side_effect=replace_after_read), \
                     patch.object(workflow, 'run_capture', return_value=receipt):
                    try:
                        workflow.run(self.fixture, self.bootstrap, self.engine, output, only='positive-1')
                    except runner.CaptureError:
                        pass
                report = json.loads((output / 'acceptance.json').read_text())
                self.assertTrue(replaced)
                self.assertEqual(report[digest_key], hashlib.sha256(raw).hexdigest())
                self.assertEqual(report['status'], 'failed')


class CaptureIntegrationTests(unittest.TestCase):
    """Use the existing inert capture fixture; child execution stays synthetic."""
    def setUp(self):
        self.fixture = capture_fixtures.CaptureRunnerTests()
        self.fixture.setUp(); self.addCleanup(self.fixture.tearDown)

    def test_baseline_capture_ignores_inherited_playback_environment(self):
        def execute(*args, **kwargs):
            self.assertNotIn('OPENJKDF2_PLAY_REQUEST', args[2])
            return self.fixture.synthetic_capture(*args, **kwargs)
        with patch.dict(os.environ, {'OPENJKDF2_PLAY_REQUEST': 'inherited-unreviewed-request'}):
            result = self.fixture.run_request(executor=execute)
        self.assertEqual(result['status'], 'completed')
        self.assertNotIn('requestedGameplay', result)
        self.assertFalse((self.fixture.output / 'play-request.txt').exists())

    def prepare_play(self):
        self.fixture.request.update(gameplay=independent_program(), engineTimeoutMs=10000,
                                    runnerTimeoutMs=12000)

    def test_opt_in_playback_has_separate_reviewed_request_and_raw_receipt(self):
        self.prepare_play()
        def execute(*args, **kwargs):
            output, environment = args[1], args[2]
            self.assertEqual(Path(environment['OPENJKDF2_PLAY_REQUEST']), output / 'play-request.txt')
            (output / 'play-telemetry.json').write_text(json.dumps(independent_trace()))
            return self.fixture.synthetic_capture(*args, **kwargs)
        result = self.fixture.run_request(executor=execute)
        self.assertEqual(result['status'], 'completed', result.get('error'))
        self.assertTrue(result['inputsUnchanged'])
        self.assertEqual(result['gameplay']['observed'], independent_trace())

    def test_corrupt_telemetry_and_changed_play_request_leave_failed_receipts(self):
        self.prepare_play()
        for tamper_request in (False, True):
            with self.subTest(tamper_request=tamper_request):
                self.fixture.output = self.fixture.root / ('tamper-' + str(tamper_request))
                def execute(*args, **kwargs):
                    output = args[1]
                    trace = independent_trace()
                    if tamper_request:
                        with (output / 'play-request.txt').open('a') as stream: stream.write('changed')
                    else: trace['completed'] = False
                    (output / 'play-telemetry.json').write_text(json.dumps(trace))
                    return self.fixture.synthetic_capture(*args, **kwargs)
                result = self.fixture.run_request(executor=execute)
                self.assertEqual(result['status'], 'failed')
                self.assertTrue(result['inputsUnchanged'])
                self.assertTrue((self.fixture.output / 'launcher-receipt.json').is_file())


if __name__ == '__main__':
    unittest.main()
