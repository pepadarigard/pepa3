import {
  BellRing,
  Box,
  Camera,
  CircleX,
  Fingerprint,
  Globe,
  Keyboard,
  Link,
  MousePointerClick,
  ScanSearch,
  TextCursorInput,
  Lock,
  LockOpen,
  Repeat,
  Rocket,
  Send,
  Workflow,
  Copy,
  FileInput,
  FilePenLine,
  FileSearch,
  FileText,
  Flag,
  FolderPlus,
  GitBranch,
  ListTree,
  Play,
  ScrollText,
  Timer,
  Trash2,
  Variable,
  type LucideIcon,
  type LucideProps,
} from "lucide-react";

/** Значки блоков по имени из описания типа (только используемые — чтобы не тянуть весь набор). */
const nodeIcons: Record<string, LucideIcon> = {
  play: Play,
  flag: Flag,
  "git-branch": GitBranch,
  variable: Variable,
  "scroll-text": ScrollText,
  timer: Timer,
  "file-pen-line": FilePenLine,
  "file-text": FileText,
  copy: Copy,
  "file-input": FileInput,
  "trash-2": Trash2,
  "folder-plus": FolderPlus,
  "file-search": FileSearch,
  "list-tree": ListTree,
  box: Box,
  "bell-ring": BellRing,
  send: Send,
  lock: Lock,
  "lock-open": LockOpen,
  workflow: Workflow,
  rocket: Rocket,
  repeat: Repeat,
  globe: Globe,
  fingerprint: Fingerprint,
  link: Link,
  "mouse-pointer-click": MousePointerClick,
  "text-cursor-input": TextCursorInput,
  keyboard: Keyboard,
  "scan-search": ScanSearch,
  camera: Camera,
  "circle-x": CircleX,
};

export function NodeIcon({ name, ...props }: { name: string } & LucideProps) {
  const Component = nodeIcons[name] ?? Box;
  return <Component {...props} />;
}

export function categoryColor(category: string): string {
  switch (category) {
    case "logic":
      return "var(--cat-logic)";
    case "files":
      return "var(--cat-files)";
    case "flow":
      return "var(--cat-flow)";
    case "browser":
      return "var(--cat-browser)";
    default:
      return "var(--cat-other)";
  }
}
