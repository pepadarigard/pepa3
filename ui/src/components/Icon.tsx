import {
  Merge,
  Split,
  AppWindow,
  BellRing,
  Code,
  ListChecks,
  Paperclip,
  SquareCheck,
  Box,
  Camera,
  CircleX,
  CloudDownload,
  CloudUpload,
  Download,
  Fingerprint,
  Globe,
  Grid2x2Check,
  Grid2x2Plus,
  Keyboard,
  Link,
  MessageCircle,
  MessageCircleQuestionMark,
  MousePointerClick,
  ScanSearch,
  TextCursorInput,
  TextSearch,
  Lock,
  LockOpen,
  Mail,
  Repeat,
  Rocket,
  Send,
  Workflow,
  Copy,
  FileInput,
  FilePenLine,
  FileSearch,
  FileClock,
  FileText,
  Flag,
  FolderPlus,
  GitBranch,
  ListTree,
  Play,
  ScrollText,
  Sheet,
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
  split: Split,
  merge: Merge,
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
  "file-clock": FileClock,
  "list-tree": ListTree,
  box: Box,
  "bell-ring": BellRing,
  send: Send,
  "message-circle": MessageCircle,
  "message-circle-question": MessageCircleQuestionMark,
  "list-checks": ListChecks,
  "square-check": SquareCheck,
  paperclip: Paperclip,
  "app-window": AppWindow,
  code: Code,
  "text-search": TextSearch,
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
  download: Download,
  "cloud-download": CloudDownload,
  "cloud-upload": CloudUpload,
  sheet: Sheet,
  mail: Mail,
  "grid-2x2-plus": Grid2x2Plus,
  "grid-2x2-check": Grid2x2Check,
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
    case "web":
      return "var(--cat-web)";
    case "tables":
      return "var(--cat-tables)";
    case "email":
      return "var(--cat-email)";
    case "telegram":
      return "var(--cat-telegram)";
    case "desktop":
      return "var(--cat-desktop)";
    default:
      return "var(--cat-other)";
  }
}
