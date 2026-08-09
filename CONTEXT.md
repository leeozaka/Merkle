# Test Impact Planning

This context defines the language used to explain changes, evidence, and advisory test plans. It describes the problem domain without choosing an implementation, storage system, language runtime, or command-line shape.

## Repository change

**Repository**:
The source, test, and configuration material placed within one analysis boundary.
_Avoid_: Codebase, workspace, project

**Snapshot**:
An immutable view of all repository inputs relevant to one analysis.
_Avoid_: Version, checkout, tree

**Baseline**:
The snapshot used as the reference point for comparison.
_Avoid_: Old version, parent, target

**Candidate**:
The snapshot whose effects are being evaluated against a baseline.
_Avoid_: New version, current branch, head

**Source Unit**:
The smallest source element that an active language adapter can identify consistently, such as a file, type, or member.
_Avoid_: Node, leaf, component

**Stable Unit Identity**:
The canonical identity used to recognize the same source unit across compatible snapshots.
_Avoid_: Symbol name, path, hash

**Change Frontier**:
The smallest set of source units sufficient to explain the difference between a baseline and candidate.
_Avoid_: Changed files, diff, hash collision

**Impact Path**:
An explainable sequence of relationships from a changed source unit to a candidate test.
_Avoid_: Dependency chain, blast radius

## Tests and plans

**Test Identity**:
The canonical identity used to recognize one discoverable test across compatible runs.
_Avoid_: Test name, display name, selector string

**Requested Test**:
A test proposed by a language adapter because it relates to one or more changed source units.
_Avoid_: Affected test, required test

**Test Plan**:
A ranked set of selected tests, exclusions, evidence, estimates, warnings, and the policy used to produce the recommendation.
_Avoid_: Test list, suite, run

**Full Suite**:
The repository's complete configured regression test set for the relevant scope.
_Avoid_: All tests, complete coverage

**Unmapped Unit**:
A changed source unit for which no known test relationship is available.
_Avoid_: Untested code, safe change, uncovered code

**Plan Recommendation**:
The advisory choice among a selected plan, the full suite, plan-only review, or a policy failure.
_Avoid_: Guarantee, verdict, approval

## Evidence and learning

**Evidence**:
A traceable fact supporting or weakening the relationship between a changed source unit and a test.
_Avoid_: Proof, score, signal

**Observation**:
Evidence that a particular test execution reached a particular source unit in a compatible run.
_Avoid_: Coverage, trace, hit

**Historical Sample**:
A terminal run record whose changes, executed tests, durations, outcomes, and provenance can inform later plans.
_Avoid_: Training data, build log, history entry

**Official Run**:
A historical sample admitted through an explicitly trusted team execution path.
_Avoid_: Cloud run, production run, server run

**Local Run**:
A historical sample produced from a developer's local repository state and retained with local provenance.
_Avoid_: Untrusted run, dev run, unofficial run

**Impact Probability**:
The estimated likelihood that a test is relevant to the supplied change event, conditional on the available evidence.
_Avoid_: Confidence, certainty, failure probability

**Evidence Confidence**:
The assessed completeness, compatibility, amount, and recency of evidence behind an impact probability.
_Avoid_: Impact probability, accuracy, guarantee

**Expected Duration**:
The estimated execution time of a test or plan under comparable conditions.
_Avoid_: Timeout, deadline, latency limit

**Compatible History**:
Historical samples whose repository, identity, adapter, and execution context can be compared with the current analysis.
_Avoid_: Valid history, matching builds, usable logs

## Language support

**Language Adapter**:
A provider that translates one language ecosystem's source and test concepts into the shared test-impact domain.
_Avoid_: Plugin, runner, integration

**Capability**:
A named adapter operation that can be requested and verified before analysis begins.
_Avoid_: Feature, mode, flag

**Minimal Adapter**:
A language adapter that can map changed source units to requested tests and explain those mappings.
_Avoid_: Basic adapter, partial adapter, file adapter

**Deep Adapter**:
A language adapter that can also discover, observe, execute, and report tests for its ecosystem.
_Avoid_: Full adapter, official adapter, complete adapter

## State lifecycle

**Run Journal**:
The isolated, not-yet-published record of an analysis or execution in progress.
_Avoid_: Current state, partial result, log file

**Terminal Report**:
The immutable result published after a run succeeds or fails.
_Avoid_: Latest state, output file, test log

**State Publication**:
The atomic transition that makes one terminal report and its admitted evidence visible to other users or agents.
_Avoid_: Save, commit, sync
