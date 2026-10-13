import { API_BASE } from '@/config/env';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button } from '@/components/ui';
import { apiJson } from '@/services/api';
import { workItemSummary, type FindingWorkItem } from '@/utils/work-items';

/** Azure Boards link of one finding: create, follow a queued creation, open, or retry after a refusal. */
export function IssueWorkItem({ findingId, authToken }: { findingId: string; authToken?: string }) {
  const client = useQueryClient();
  const key = ['collaboration', 'work-item', findingId];
  const path = `${API_BASE}/v1/collaboration/findings/${encodeURIComponent(findingId)}/work-item`;
  const item = useQuery({
    queryKey: key,
    // The API answers null while the finding has no work item: a normal state, not a failure.
    queryFn: ({ signal }) => apiJson<FindingWorkItem | null>(path, authToken, { signal }),
    refetchInterval: (query) => (query.state.data?.state === 'pending' ? 5000 : false),
  });
  const request = useMutation({
    mutationFn: () => apiJson<FindingWorkItem>(path, authToken, { method: 'POST' }),
    onSuccess: (result) => client.setQueryData(key, result),
  });
  const data = item.data;

  return (
    <div className="issue-work-item" aria-live="polite">
      <h4>Azure Boards</h4>
      {item.isLoading && <p role="status">Loading work item...</p>}
      {(item.error ?? request.error) && (
        <p className="feedback error" role="alert">
          {(item.error ?? request.error)?.message}
        </p>
      )}
      {data && (
        <p>
          {data.state === 'created' && data.url ? (
            <a href={data.url} target="_blank" rel="noreferrer">
              {workItemSummary(data)} ↗
            </a>
          ) : (
            workItemSummary(data)
          )}
        </p>
      )}
      {item.isSuccess && (!data || data.state === 'failed') && (
        <Button type="button" disabled={request.isPending} onClick={() => request.mutate()}>
          {request.isPending ? 'Requesting...' : data ? 'Retry work item' : 'Create work item'}
        </Button>
      )}
    </div>
  );
}
