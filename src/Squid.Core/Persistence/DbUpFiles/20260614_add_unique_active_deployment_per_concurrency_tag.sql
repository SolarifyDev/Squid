-- Enforce the cross-process (multi-pod) concurrency-slot invariant at the database level:
-- at most ONE ACTIVE task per ConcurrencyTag, where "active" = Executing, Paused, or
-- Cancelling. Paused and Cancelling MUST count as occupying the slot: a transient-infra pause
-- or a wall-clock timeout transitions Executing→Paused while DELIBERATELY leaving the in-flight
-- agent script running (the pointer is preserved so a resume re-attaches). If the slot only
-- counted Executing, a second same-environment deployment could start while the paused one's
-- script is still live on the agent — the exact overlap this guard exists to prevent.
--
-- Before this, same-environment serialization relied on WaitForConcurrencySlotAsync, a
-- non-atomic check-then-act poll that "proceeded anyway" on timeout. With multiple server pods
-- (K8s Deployment, dynamic replicas) two pods could both observe a free slot and both run a
-- deployment to the same environment. The runner's poll stays as a fast path, but THIS partial
-- unique index is the load-bearing guarantee: the only way a second row enters the active set
-- for a tag is a Pending/Paused→Executing transition, which then fails with 23505 → mapped to
-- ConcurrencySlotOccupiedException and re-enqueued (never overlapped, never lost).
--
-- Pre-flight: a database that ran the pre-fix racy code MAY already hold >1 active row for one
-- tag (e.g. an Executing one plus a Paused-with-running-script one). The unique index cannot
-- build over duplicates, so keep the earliest-started active row and administratively FAIL the
-- rest — these are anomalous overlaps that should never have coexisted; failing them (with a
-- clear message) is the honest resolution and the operator can redeploy. Idempotent: a DB with
-- no duplicates is untouched, and CREATE UNIQUE INDEX IF NOT EXISTS is re-runnable.
--
-- The index is built non-CONCURRENTLY (CONCURRENTLY is incompatible with DbUp's single
-- transaction). It briefly takes a SHARE lock on server_task during the build, blocking writes
-- on still-serving old pods during a rolling upgrade. server_task is retention-pruned (bounded)
-- and the index is partial (only active rows), so the build is fast and the window is brief.
UPDATE server_task
SET state = 'Failed',
    completed_time = now(),
    last_modified_date = now(),
    error_message = 'Auto-resolved on upgrade: multiple concurrent deployments shared one concurrency tag (pre-fix race). The earliest was kept active; this duplicate was failed. Redeploy if needed.'
WHERE id IN (
    SELECT id FROM (
        SELECT id,
               row_number() OVER (PARTITION BY concurrency_tag ORDER BY start_time NULLS LAST, id) AS rn
        FROM server_task
        WHERE concurrency_tag IS NOT NULL AND state IN ('Executing', 'Paused', 'Cancelling')
    ) ranked
    WHERE ranked.rn > 1
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_server_task_active_per_tag
    ON server_task (concurrency_tag)
    WHERE concurrency_tag IS NOT NULL AND state IN ('Executing', 'Paused', 'Cancelling');
