interface StatusBadgeProps {
  status: string;
  className?: string;
}

const statusConfig: Record<string, { bg: string; dot: string; text: string }> = {
  active: { bg: 'bg-accent-green/10', dot: 'bg-accent-green', text: 'text-accent-green' },
  running: { bg: 'bg-accent-green/10', dot: 'bg-accent-green', text: 'text-accent-green' },
  idle: { bg: 'bg-text-secondary/10', dot: 'bg-text-secondary', text: 'text-text-secondary' },
  stopped: { bg: 'bg-accent-red/10', dot: 'bg-accent-red', text: 'text-accent-red' },
  error: { bg: 'bg-accent-red/10', dot: 'bg-accent-red', text: 'text-accent-red' },
  inactive: { bg: 'bg-text-secondary/10', dot: 'bg-text-secondary', text: 'text-text-secondary' },
};

const defaultConfig = { bg: 'bg-text-secondary/10', dot: 'bg-text-secondary', text: 'text-text-secondary' };

export default function StatusBadge({ status, className = '' }: StatusBadgeProps) {
  const config = statusConfig[status.toLowerCase()] || defaultConfig;
  return (
    <span className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium ${config.bg} ${config.text} ${className}`}>
      <span className={`w-1.5 h-1.5 rounded-full ${config.dot}`} />
      {status}
    </span>
  );
}
