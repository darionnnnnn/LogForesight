"""Static safety contract for the public-route PowerShell orchestrator (no Web process)."""
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("Prepare-Qualification.ps1").read_text(encoding="utf-8")
MODULE = Path(__file__).with_name("E2Contract.psm1").read_text(encoding="utf-8")
PREFLIGHT = Path(__file__).with_name("e2_preflight.py").read_text(encoding="utf-8")


class PreparationScriptContractTests(unittest.TestCase):
    def test_public_settings_binding_pilot_and_job_routes_are_explicit(self):
        for route in (
            "api/admin/settings",
            "api/admin/settings/prtg",
            "api/prtg/monitoring",
            "api/prtg/monitoring/estimate",
            "api/prtg/monitoring/source-preview",
            "api/prtg/monitoring/trusted-sampling/qualification-jobs/contract",
            "api/prtg/monitoring/trusted-sampling/profiles?offset=",
            "api/prtg/monitoring/trusted-sampling/bindings/batch",
            "api/prtg/monitoring/trusted-sampling/qualification-jobs/pilot",
            "api/prtg/monitoring/trusted-sampling/qualification-jobs/start",
            "api/prtg/monitoring/trusted-sampling/qualification-jobs/current",
        ):
            with self.subTest(route=route):
                self.assertIn(route, SCRIPT)
        self.assertIn("qualification-jobs/$JobId/resume", SCRIPT)
        self.assertIn("SettingsPlanPath", SCRIPT)
        self.assertIn("MonitoringPlanPath", SCRIPT)
        self.assertIn("policyRevisionNow -cne [string]$monitoringPlan.expectedRevision", SCRIPT)
        self.assertIn("continuationAllowed", SCRIPT)
        self.assertIn("api/admin/prtg-runtime-ownership/status", SCRIPT)
        self.assertIn("runtimeMetadata=$runtimeMetadata", SCRIPT)
        self.assertIn("webAssemblyMvid", SCRIPT)
        self.assertIn("coreAssemblyMvid", SCRIPT)
        self.assertIn("descriptorSha256=$DescriptorHash", SCRIPT)

    def test_revision_snapshot_precedes_binding_writes(self):
        seal = SCRIPT.index("live-revision-fences-sealed")
        batch_write = SCRIPT.index("for($start=0;$start -lt $FixedSensors;$start+=100)")
        self.assertLess(seal, batch_write)
        self.assertIn("Saved partial receipt has a stale", MODULE)
        self.assertIn("@('sourceGeneration',$sourceGeneration)", SCRIPT)
        self.assertIn("@('authorityContextFingerprint',$authority)", SCRIPT)

    def test_partial_binding_and_unknown_job_outcomes_are_receipted(self):
        self.assertIn("bindingRejected", SCRIPT)
        self.assertIn("outcome-unknown", SCRIPT)
        self.assertIn("jobStartOutcomeUnknown=$true", SCRIPT)
        self.assertIn("unknown-start-explicitly-reconciled", SCRIPT)
        self.assertIn("committedSensorObjids", SCRIPT)

    def test_resume_requires_a_complete_exact_current_binding_catalog(self):
        self.assertIn("Assert-E2ResumeBindingsComplete $profileRows.ToArray()", SCRIPT)
        self.assertIn("Set-E2BindingBatchReceipt", SCRIPT)
        self.assertIn("every current binding to match the sealed plan", MODULE)
        self.assertIn("Assert-E2CurrentJobPages $JobId $current $ExpectedVersion $ExpectedWave", SCRIPT)
        self.assertIn("qualification-jobs/$JobId/page?offset=$offset&limit=$limit", SCRIPT)
        self.assertIn("Assert-E2StableJobSnapshot $Before $After $JobId $Version $Wave", SCRIPT)
        self.assertIn("Assert-E2ResumeProfileProgress $Profiles $Manifest $Plans $rows.ToArray()", SCRIPT)
        self.assertIn("AllowPolicyRevisionProgress:$allowMessageDateFormatDrift", SCRIPT)
        self.assertIn("Assert-E2SealedResumeManifest $manifest $receipt", SCRIPT)
        self.assertNotIn("$manifest @($bindingPlan.rows) $Invoke-Api", SCRIPT)

    def test_attempt_and_deadline_caps_are_fixed(self):
        self.assertIn("durationHours=720;maximumAttempts=1", SCRIPT)
        self.assertIn("New-E2QualificationResumeRequest $ExpectedVersion", SCRIPT)
        self.assertIn("CancelAfter([TimeSpan]::FromSeconds(30))", SCRIPT)
        self.assertIn("FromSeconds($MaxSeconds)", SCRIPT)

    def test_listener_identity_is_rechecked_and_frozen_per_run(self):
        self.assertIn("processStartUtcTicks", SCRIPT)
        self.assertIn("$currentOwner=Assert-OwnedListener", SCRIPT)
        self.assertIn("$currentOwner[$name] -cne [string]$owner[$name]", SCRIPT)
        self.assertIn("Test-E2BootstrapRequired $Action", SCRIPT)
        self.assertIn("Assert-E2BootstrapReadback $settings $desiredSettingsFields $true", SCRIPT)
        self.assertIn("guarded-source-enable-not-applied-by-exact-disabled-readback", SCRIPT)
        for operation in ("Settings", "SourcePolicy", "Binding", "AdmissionPilot", "QualificationPilot"):
            with self.subTest(operation=operation):
                self.assertIn(f"Assert-E2ActionMayWrite $Action '{operation}'", SCRIPT)

    def test_runtime_verifier_binds_loaded_process_config_and_disk_metadata(self):
        verifier = Path(__file__).with_name("verify_runtime_owner.py").read_text(encoding="utf-8")
        for field in ("runtimeMetadata", "databasePath", "descriptorSha256", "webAssemblyLocation",
                      "coreAssemblyLocation", "webAssemblyMvid", "coreAssemblyMvid"):
            with self.subTest(field=field):
                self.assertIn(field, verifier)
        self.assertIn("PE metadata without loading or executing", verifier)

    def test_preflight_identity_is_stable_and_sha_encoding_is_normalized(self):
        self.assertIn("sourceIdentitySha256", PREFLIGHT)
        self.assertNotIn("databaseBytes", PREFLIGHT[PREFLIGHT.index('stable_identity = {'):PREFLIGHT.index('identity_sha =')])
        self.assertIn("ConvertTo-E2Sha256", SCRIPT)
        self.assertIn("ToUpperInvariant()", MODULE)
        self.assertNotIn("e2-source-preflight.json", SCRIPT)

    def test_monitoring_reference_is_checked_in_both_preflight_routes_before_writes(self):
        python_preflight = SCRIPT.index("--monitoring-plan $monitoringPlanPath")
        first_api_request = SCRIPT.index("$settings=Invoke-Api GET")
        self.assertLess(python_preflight, first_api_request)
        self.assertIn("Test-E2SafeTimeBasisReference $monitoringPlan.timeBasisEvidenceReference", SCRIPT)
        self.assertIn("value.encode(\"utf-16-le\"", PREFLIGHT)
        self.assertIn("--monitoring-plan", PREFLIGHT)


if __name__ == "__main__":
    unittest.main()
