# Fills the demo quartz.db with a realistic week of runs for the manual screenshots (Contoso demo data only).
import sqlite3, sys, uuid, random, json
from datetime import datetime, timedelta, timezone

db = sys.argv[1]
random.seed(42)
from datetime import tzinfo
class Warsaw(tzinfo):
    """CET/CEST for October 2026 (DST ends 25.10.2026 01:00 UTC)."""
    SWITCH = datetime(2026, 10, 25, 1, 0, tzinfo=timezone.utc)
    def utcoffset(self, dt):
        if dt.tzinfo is self:
            utc = (dt - timedelta(hours=2)).replace(tzinfo=timezone.utc)
            return timedelta(hours=2) if utc < self.SWITCH else timedelta(hours=1)
        return timedelta(hours=1)
    def fromutc(self, dt):
        utc = dt.replace(tzinfo=timezone.utc)
        return (utc + (timedelta(hours=2) if utc < self.SWITCH else timedelta(hours=1))).replace(tzinfo=self)
    def dst(self, dt): return timedelta(0)
    def tzname(self, dt): return "Europe/Warsaw"
zone = Warsaw()
EPOCH = datetime(1, 1, 1, tzinfo=timezone.utc)
def ticks(dt): return int((dt - EPOCH).total_seconds() * 10_000_000)
def dur_ticks(seconds): return int(seconds * 10_000_000)

SUCCEEDED, FAILED, CANCELLED, SKIPPED = 0, 1, 2, 3
now = datetime.now(timezone.utc).replace(second=0, microsecond=0)
start = now - timedelta(days=7)
weekday = lambda t: t.astimezone(zone).weekday() < 5
hour = lambda t: t.astimezone(zone).hour

def every(minutes, window=None, workdays=False, offset=0):
    t = start.astimezone(zone).replace(minute=0, second=0) + timedelta(minutes=offset)
    t = t.astimezone(timezone.utc)
    while t < now:
        local = t.astimezone(zone)
        ok = (not workdays or local.weekday() < 5) and (window is None or window[0] <= local.hour * 60 + local.minute <= window[1])
        if ok: yield t
        t += timedelta(minutes=minutes)

def daily(h, m=0, workdays=False):
    d = start.astimezone(zone).date()
    while True:
        local = datetime(d.year, d.month, d.day, h, m, tzinfo=zone)
        t = local.astimezone(timezone.utc)
        if t >= now: return
        if t >= start and (not workdays or local.weekday() < 5): yield t
        d += timedelta(days=1)

def hours_of_day(hs, workdays=False):
    for h in hs:
        yield from daily(h, 0, workdays)

exec_id = lambda project, t: f"{project}-{t.astimezone(zone):%Y-%m-%d_%H-%M-%S}-{uuid.UUID(int=random.getrandbits(128))}"

def export(project, folder, wait):
    def run(t):
        s = random.randint(*wait)
        eid = exec_id(project, t)
        return (SUCCEEDED, s + 2, f"Exported {project} (Succeeded) to {t.astimezone(zone):%Y-%m-%d_%H-%M-%S}-0001.zip in 00:00:{s:02d}",
                {"ExecutionId": eid, "Status": "Succeeded", "File": f"{folder}\\{t.astimezone(zone):%Y-%m-%d_%H-%M-%S}-0001.zip", "ExtractedFiles": 1, "WaitSeconds": s, "StatusChecks": 3 + s // 15, "DurationSeconds": s + 2},
                [f"info RIS.Jobs.Export: Starting export of \"{project}\" for company \"USMF\"", f"info RIS.Jobs.Export: Execution {eid} succeeded after {3 + s // 15} status checks ({s} s)",
                 f"info RIS.Jobs.Export: Package downloaded to {folder}, 1 file extracted"])
    return run

def importer(t):
    files = random.choice([0, 0, 0, 1, 1, 2, 3]) if hour(t) in range(8, 17) else random.choice([0, 0, 0, 0, 1])
    if files == 0: return (SKIPPED, 0.4, "No input files", {"Files": 0}, ["info RIS.Jobs.Import: No input files in D:\\Integrations\\VendorInvoices\\In"])
    return (SUCCEEDED, 1.5 + files, f"Imported files: {files} succeeded, 0 failed", {"Succeeded": files, "Failed": 0, "Skipped": 0, "Pending": 0, "DataProject": "Vendor invoices", "DurationSeconds": 2 + files},
            [f"info RIS.Jobs.Import: Found {files} file(s) in input folder D:\\Integrations\\VendorInvoices\\In", "info RIS.Jobs.Import: Data project Vendor invoices: Vendor invoice header (CSV-Unicode), Vendor invoice lines (CSV-Unicode)",
             f"info RIS.Jobs.Import: {files} file(s) enqueued; status files written for the monitor"])

def monitor(kind):
    def run(t):
        checked = random.choice([0, 0, 1, 1, 2]) if hour(t) in range(8, 18) else 0
        if checked == 0: return (SKIPPED, 0.3, "Nothing to check", {"Checked": 0}, [f"info RIS.Jobs.{kind}: No status files to check"])
        return (SUCCEEDED, 1.2, f"Checked {'imports' if kind == 'ExecutionMonitor' else 'uploads'}: {checked} succeeded, 0 failed", {"Succeeded": checked, "Failed": 0, "InProgress": 0},
                [f"info RIS.Jobs.{kind}: {checked} execution(s) finished in D365 and moved to processed"])
    return run

def upload(t):
    files = random.choice([0, 1, 2, 4]) if hour(t) in range(7, 22) else 0
    if files == 0: return (SKIPPED, 0.6, "No input files", {"Files": 0}, ["info RIS.Jobs.Upload: SFTP /outbound/orders: 0 file(s) downloaded", "info RIS.Jobs.Upload: No input files"])
    return (SUCCEEDED, 2 + files * 0.5, f"Uploaded files: {files} succeeded, 0 failed", {"Succeeded": files, "Failed": 0, "ActivityId": "6f1c2a8e-3b4d-4e5f-9a10-112233445566"},
            [f"info RIS.Jobs.Upload: SFTP /outbound/orders: {files} file(s) downloaded", f"info RIS.Jobs.Upload: {files} message(s) enqueued to D365"])

def download(t):
    return (SUCCEEDED, 3.1, "Downloaded messages: 1 succeeded, 0 failed", {"Succeeded": 1, "Failed": 0, "ActivityId": "a1b2c3d4-e5f6-4711-8899-aabbccddeeff"},
            ["info RIS.Jobs.Download: Message dequeued", "info RIS.Jobs.Download: Package saved to D:\\Integrations\\Bank\\Out and acknowledged"])

jobs = [
    ("Export jobs", "CustInvoiceJour", daily(2), export("CustInvoiceJour", "D:\\Integrations\\Invoices\\Out", (180, 420))),
    ("Export jobs", "Customers", every(240), export("Customers", "D:\\Integrations\\CRM\\Out", (30, 80))),
    ("Export jobs", "Vendors V2", daily(2, 30), export("Vendors V2", "D:\\Integrations\\Vendors\\Out", (40, 110))),
    ("Warehouse", "Shipments", every(5), export("Shipments", "D:\\Integrations\\Warehouse\\Out", (6, 18))),
    ("Finance", "Vendor invoices import", every(15, (7 * 60, 18 * 60), True), importer),
    ("Finance", "Vendor invoices monitor", every(5, (7 * 60, 19 * 60), True), monitor("ExecutionMonitor")),
    ("Sales", "Sales orders upload", every(10), upload),
    ("Sales", "Sales orders monitor", every(5), monitor("ProcessingMonitor")),
    ("Finance", "Customer payments download", hours_of_day([6, 12, 18], True), download),
    ("Finance", "Exchange rates", every(60, (8 * 60, 16 * 60), True), export("Exchange rates", "D:\\Integrations\\Treasury\\Out", (20, 50))),
]

# A few typical failures, and one job failing right now (dashboard "needs attention").
failures = {
    ("Export jobs", "CustInvoiceJour", 3): ("[RIS-EXP-001] Export of CustInvoiceJour did not finish within 12:00:00 (status Executing, execution CustInvoiceJour-…). The execution may still finish in D365; check Data management.", 43200),
    ("Warehouse", "Shipments", 400): ("[RIS-HTTP-002] D365 request GetExecutionSummaryStatus failed with HTTP 503 ServiceUnavailable: The service is temporarily unavailable.", 34),
    ("Warehouse", "Shipments", 401): ("[RIS-HTTP-002] D365 request ExportToPackage failed with HTTP 503 ServiceUnavailable: The service is temporarily unavailable.", 3),
    ("Finance", "Vendor invoices import", 120): ("[RIS-IMP-004] Files do not match the entities of data project 'Vendor invoices' (Vendor invoice header, Vendor invoice lines): entities without file: Vendor invoice lines. File 'batch-0815.zip' moved to errors.", 2),
}
recent_failures = 2

c = sqlite3.connect(db)
c.execute("delete from QRTZ_EXECUTION_HISTORY"); c.execute("delete from QRTZ_JOB_STATUS"); c.execute("delete from QRTZ_MISFIRE_HISTORY")
fire_counter = 1360186449508
for group, name, times, make in jobs:
    times = list(times)
    status = dict(first=None, last=None, last_result=None, last_run=0, last_entry=None, last_summary=None, last_success=None, last_failure=None, last_failure_message=None, consecutive=0, runs=0, failures=0)
    for i, t in enumerate(times):
        t = t + timedelta(milliseconds=random.randint(20, 900))
        result, seconds, summary, metrics, log = make(t)
        error = None
        if (group, name, i) in failures:
            summary, seconds = failures[(group, name, i)]; result = FAILED; error = summary[summary.index(']') + 2:]
            metrics = {"ErrorCode": summary[1:summary.index(']')]}
            log = log[:1] + [f"fail RIS.Jobs: {summary}"]
        if name == "Sales orders upload" and i >= len(times) - recent_failures:
            summary = "[RIS-SFTP-001] SFTP sftp.contoso.com:22 (ris-integration): connection failed: No connection could be made because the target machine actively refused it."
            result, seconds, error, metrics = FAILED, 21, summary[16:], {"ErrorCode": "RIS-SFTP-001"}
            log = ["info RIS.Jobs.Upload: Downloading /outbound/orders/*.csv from sftp.contoso.com", f"fail RIS.Jobs.Upload: {summary}"]
        entry = uuid.uuid4().hex
        fire_counter += random.randint(1000, 90000)
        lines = "\n".join(f"{(t + timedelta(seconds=k * max(seconds, 1) / max(len(log), 1))).strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3]}Z {line}" for k, line in enumerate([f'info RIS.Jobs: Job "{group}.{name}" started (environment https://contoso-prod.operations.dynamics.com/)'] + log))
        c.execute("insert into QRTZ_EXECUTION_HISTORY values (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                  ("QuartzScheduler", entry, "NON_CLUSTERED", name, group, f"{name} trigger", group, ticks(t), dur_ticks(seconds),
                   1 if result != FAILED else 0, error, 0, 0, lines, result, summary, json.dumps(metrics), 0, f"NON_CLUSTERED{fire_counter}", None, 0))
        status["first"] = status["first"] or ticks(t); status["last"] = ticks(t); status["last_result"] = result; status["last_run"] = dur_ticks(seconds)
        status["last_entry"] = entry; status["last_summary"] = summary; status["runs"] += 1
        if result == FAILED:
            status["failures"] += 1; status["consecutive"] += 1; status["last_failure"] = ticks(t); status["last_failure_message"] = summary
        else:
            status["consecutive"] = 0
            if result == SUCCEEDED: status["last_success"] = ticks(t)
    if status["runs"]:
        c.execute("insert into QRTZ_JOB_STATUS values (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                  ("QuartzScheduler", group, name, status["first"], status["last"], status["last_result"], status["last_run"], "NON_CLUSTERED", status["last_entry"],
                   status["last_summary"], status["last_success"], status["last_failure"], status["last_failure_message"], status["consecutive"], status["runs"], status["failures"]))
    print(f"{group}.{name}: {status['runs']} runs, {status['failures']} failed")
c.commit()
