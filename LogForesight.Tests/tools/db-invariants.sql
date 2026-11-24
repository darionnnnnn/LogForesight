-- 交辦資料一致性檢查（docs/FEEDBACK-81-CONCURRENCY-PLAN.md 第 6.1 節）。
-- 對站台的 SQLite 資料庫副本執行：sqlite3 <副本.db> < db-invariants.sql
-- 每一列的數字應為 0；非 0 代表留下了不一致的資料（先複製資料庫再查，不要對執行中的站台直接跑）。
select 'a 進行中案件但所屬單已結案或沒有單', count(*) from lf_issue_cases c left join lf_work_orders o on o.work_order_id=c.work_order_id where c.closed_at is null and (o.work_order_id is null or o.closed_at is not null);
select 'b 進行中的單但沒有進行中成員', count(*) from lf_work_orders o where o.closed_at is null and not exists(select 1 from lf_issue_cases c where c.work_order_id=o.work_order_id and c.closed_at is null);
select 'c 同主機同問題多件進行中案件', count(*) from (select host_name_key, issue_key from lf_issue_cases where closed_at is null group by host_name_key, issue_key having count(*)>1);
select 'd 同處理人同問題多張進行中單', count(*) from (select handler_id, source_key, event_id from lf_work_orders where closed_at is null group by handler_id, source_key, event_id having count(*)>1);
select 'e 逐日列非結案但所屬案件已結案', count(*) from lf_issue_handling h join lf_issue_cases c on c.case_id=h.case_id where c.closed_at is not null and h.status in ('in_progress','observing','escalated');
select 'f 逐日列已結案狀態但所屬案件仍進行中', count(*) from lf_issue_handling h join lf_issue_cases c on c.case_id=h.case_id where c.closed_at is null and h.status in ('resolved','wont_fix','false_positive','known_noise');
select 'g 進行中案件沒有任何逐日列', count(*) from lf_issue_cases c where c.closed_at is null and c.day_sync_pending=0 and not exists(select 1 from lf_issue_handling h where h.case_id=c.case_id);
select 'h 案件處理人與所屬單處理人不同', count(*) from lf_issue_cases c join lf_work_orders o on o.work_order_id=c.work_order_id where c.closed_at is null and c.handler_id<>o.handler_id;
select 'i 案件狀態與結案時間矛盾', count(*) from lf_issue_cases where (closed_at is null and status in ('resolved','wont_fix','false_positive','known_noise')) or (closed_at is not null and status in ('in_progress','observing','escalated','open'));
select 'j 建立後從未有過逐日列的案件（含已結案）', count(*) from lf_issue_cases c where c.day_sync_pending=0 and not exists(select 1 from lf_issue_handling h where h.case_id=c.case_id);
