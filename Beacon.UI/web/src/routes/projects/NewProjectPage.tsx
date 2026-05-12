import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { z } from 'zod';
import { toast } from 'sonner';
import { Database, Folder, GitBranch } from 'lucide-react';
import { beaconApi } from '@/api/client';
import type { CreateProjectCommand, CreateProjectResult } from '@/api/generated/beacon-api';
import { Button, Card, CardBody, Field, Input, PageHeader, Textarea } from '@/components/beacon';
import { ApiError } from '@/lib/api';
import { useDataSourcesQuery } from '@/routes/data-sources/queries';

const SCHEMA = z.object({
  name: z.string().trim().min(1, 'Name is required').max(200),
  description: z.string().trim().max(2000),
  repositoryUrls: z.string(),
  accessToken: z.string(),
});

type FormValues = z.infer<typeof SCHEMA>;

const DEFAULTS: FormValues = {
  name: '',
  description: '',
  repositoryUrls: '',
  accessToken: '',
};

function splitLines(s: string): string[] {
  return s.split(/\r?\n/).map(x => x.trim()).filter(x => x.length > 0);
}

function useCreateProject() {
  const qc = useQueryClient();
  return useMutation<CreateProjectResult, unknown, CreateProjectCommand>({
    mutationFn: values => beaconApi().createProject(values),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['projects'] }),
  });
}

export default function NewProjectPage() {
  const navigate = useNavigate();
  const dataSources = useDataSourcesQuery();
  const createMutation = useCreateProject();
  const [selectedIds, setSelectedIds] = useState<number[]>([]);

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: DEFAULTS,
    mode: 'onTouched',
  });

  const toggleSource = (id: number) =>
    setSelectedIds(prev => prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id]);

  const onSubmit = handleSubmit(async v => {
    try {
      const result = await createMutation.mutateAsync({
        name: v.name.trim(),
        description: v.description.trim() || null,
        dataSourceIds: selectedIds,
        repositoryUrls: splitLines(v.repositoryUrls),
        accessToken: v.accessToken.trim() || null,
      });
      toast.success('Project created');
      navigate(`/projects/${result.projectId}`);
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Create failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  const sources = dataSources.data?.entries ?? [];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="nodes"
        eyebrow="Workspaces"
        prefix="New"
        emphasis="project"
        sub={
          <span className="text-text-muted">
            <Link to="/projects" className="text-text-muted">Projects</Link>
            <span className="mx-1.5">/</span>
            New
          </span>
        }
        actions={
          <Link to="/projects">
            <Button type="button">Cancel</Button>
          </Link>
        }
      />

      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        <Card>
          <CardBody>
            <h3 className="m-0 mb-3 text-sm font-semibold text-text flex items-center gap-1.5">
              <Folder size={14} /> Basics
            </h3>

            <div className="flex flex-col gap-3.5">
              <Field label={<>Project name <span className="text-crit">*</span></>}>
                <Input
                  id="np-name"
                  type="text"
                  placeholder="Customer analytics"
                  aria-invalid={!!errors.name}
                  {...register('name')}
                />
                {errors.name && <span className="text-xs text-crit">{errors.name.message}</span>}
              </Field>

              <Field label="Description">
                <Textarea
                  id="np-desc"
                  rows={3}
                  placeholder="What this project covers, who owns it, and how it's used."
                  aria-invalid={!!errors.description}
                  {...register('description')}
                />
                {errors.description && <span className="text-xs text-crit">{errors.description.message}</span>}
              </Field>
            </div>
          </CardBody>
        </Card>

        <Card>
          <CardBody>
            <h3 className="m-0 mb-1 text-sm font-semibold text-text flex items-center gap-1.5">
              <Database size={14} /> Data sources
            </h3>
            <div className="text-text-muted text-xs mb-3">
              Pick the data sources this project should be scoped to. You can change this later.
            </div>

            {dataSources.isLoading && <div className="text-text-muted">Loading data sources…</div>}

            {!dataSources.isLoading && sources.length === 0 && (
              <div className="text-text-muted text-sm">
                No data sources configured yet. <Link to="/data-sources" className="text-brand-600">Add one</Link> first.
              </div>
            )}

            {sources.length > 0 && (
              <div className="grid gap-2">
                {sources.map(ds => {
                  const checked = selectedIds.includes(ds.id);
                  return (
                    <label
                      key={ds.id}
                      className={`border border-border rounded-md shadow-sm px-3 py-2.5 flex items-center gap-3 cursor-pointer ${checked ? 'bg-surface-2' : 'bg-surface'}`}
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        onChange={() => toggleSource(ds.id)}
                      />
                      <div className="flex-1">
                        <div className="font-semibold">{ds.name}</div>
                        <div className="text-text-muted mono text-xs">
                          {ds.dataSourceType}
                          {ds.databaseEngineType && ` · ${ds.databaseEngineType}`}
                        </div>
                      </div>
                    </label>
                  );
                })}
              </div>
            )}
          </CardBody>
        </Card>

        <Card>
          <CardBody>
            <h3 className="m-0 mb-1 text-sm font-semibold text-text flex items-center gap-1.5">
              <GitBranch size={14} /> Repositories (optional)
            </h3>
            <div className="text-text-muted text-xs mb-3">
              One Git URL per line. Beacon scans linked repos to surface code references.
            </div>

            <div className="flex flex-col gap-3.5">
              <Field label="Repository URLs">
                <Textarea
                  id="np-repos"
                  rows={3}
                  className="mono"
                  placeholder="https://github.com/example/repo-a&#10;https://github.com/example/repo-b"
                  {...register('repositoryUrls')}
                />
              </Field>

              <Field
                label="Personal access token"
                hint="Token is encrypted at rest. Required only for private repos; leave empty for public ones."
              >
                <Input
                  id="np-token"
                  type="password"
                  className="mono"
                  placeholder="Required only for private repositories"
                  {...register('accessToken')}
                />
              </Field>
            </div>
          </CardBody>
        </Card>

        <div className="flex justify-end gap-2.5">
          <Link to="/projects">
            <Button type="button">Cancel</Button>
          </Link>
          <Button
            type="submit"
            variant="primary"
            disabled={isSubmitting || createMutation.isPending}
          >
            {createMutation.isPending ? 'Creating…' : 'Create project'}
          </Button>
        </div>
      </form>
    </div>
  );
}
