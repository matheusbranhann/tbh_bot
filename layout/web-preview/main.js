import './style.css';

// ============================================
// State
// ============================================
const state = {
  isAttached: false,
  buildHash: '9a8b7c6',
};

let statusTimer;

// DOM Elements
const canvas = document.getElementById('bg-canvas');
const ctx = canvas.getContext('2d');
const btnLaunch = document.getElementById('btnLaunch');
const connHeader = document.getElementById('connHeader');
const connLabel = document.getElementById('connLabel');
const connDetails = document.getElementById('connDetails');
const statusConsole = document.getElementById('statusConsole');

// ============================================
// Canvas Animation (Cyber Network)
// ============================================
let particles = [];
const PARTICLE_COUNT = 80;
const CONNECTION_DIST = 150;

function resizeCanvas() {
  canvas.width = window.innerWidth;
  canvas.height = window.innerHeight;
}
window.addEventListener('resize', resizeCanvas);
resizeCanvas();

class Particle {
  constructor() {
    this.x = Math.random() * canvas.width;
    this.y = Math.random() * canvas.height;
    this.vx = (Math.random() - 0.5) * 1.5;
    this.vy = (Math.random() - 0.5) * 1.5;
  }
  update() {
    this.x += this.vx;
    this.y += this.vy;
    if (this.x < 0 || this.x > canvas.width) this.vx *= -1;
    if (this.y < 0 || this.y > canvas.height) this.vy *= -1;
  }
}

for (let i = 0; i < PARTICLE_COUNT; i++) {
  particles.push(new Particle());
}

function drawCanvas() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  
  // Theme color based on connection
  const themeColor = state.isAttached ? 'rgba(255, 122, 24,' : 'rgba(251, 191, 36,'; 
  
  for (let i = 0; i < particles.length; i++) {
    const p = particles[i];
    p.update();
    
    ctx.beginPath();
    ctx.arc(p.x, p.y, 1.5, 0, Math.PI * 2);
    ctx.fillStyle = themeColor + ' 0.5)';
    ctx.fill();

    for (let j = i + 1; j < particles.length; j++) {
      const p2 = particles[j];
      const dx = p.x - p2.x;
      const dy = p.y - p2.y;
      const dist = Math.sqrt(dx*dx + dy*dy);
      
      if (dist < CONNECTION_DIST) {
        ctx.beginPath();
        ctx.moveTo(p.x, p.y);
        ctx.lineTo(p2.x, p2.y);
        ctx.strokeStyle = themeColor + ` ${(1 - dist/CONNECTION_DIST) * 0.2})`;
        ctx.lineWidth = 1;
        ctx.stroke();
      }
    }
  }
  requestAnimationFrame(drawCanvas);
}
drawCanvas();

// ============================================
// Sidebar Navigation
// ============================================
const navItems = document.querySelectorAll('.nav-item');
const views = document.querySelectorAll('.view-content');
const pageTitle = document.getElementById('pageTitle');

navItems.forEach(item => {
  item.addEventListener('click', () => {
    // Remove active
    navItems.forEach(n => n.classList.remove('active'));
    views.forEach(v => v.classList.remove('active'));
    
    // Add active
    item.classList.add('active');
    const target = item.getAttribute('data-target');
    document.getElementById(target).classList.add('active');
    
    // Update Header title
    pageTitle.textContent = item.textContent.trim();
  });
});

// ============================================
// Console Log Helper
// ============================================
function log(msg) {
  // console removed from UI, logging to browser console instead
  console.log(`sys > ${msg}`);
}

// ============================================
// Connection Logic
// ============================================
function updateConnectionUI() {
  if (state.isAttached) {
    connHeader.classList.add('connected');
    connLabel.textContent = 'LINK ESTABLISHED';
    connDetails.textContent = `Build: ${state.buildHash} | Offsets: OK`;
    btnLaunch.style.opacity = '0.5';
    btnLaunch.style.cursor = 'not-allowed';
    btnLaunch.textContent = 'Engine Running';
  } else {
    connHeader.classList.remove('connected');
    connLabel.textContent = 'OFFLINE';
    connDetails.textContent = 'Aguardando conexão...';
    btnLaunch.style.opacity = '1';
    btnLaunch.style.cursor = 'pointer';
    btnLaunch.textContent = 'Launch Engine';
  }
}

btnLaunch.addEventListener('click', () => {
  if (state.isAttached) return;
  log('Buscando processo hl2.exe / tbh_bot...');
  btnLaunch.textContent = 'Connecting...';
  
  setTimeout(() => {
    state.isAttached = true;
    updateConnectionUI();
    log('Neural link established com sucesso.');
  }, 1200);
});

// ============================================
// Modules (ACTk Chaining)
// ============================================
const actkToggle = document.getElementById('actkToggle');
const autoChains = document.querySelectorAll('.auto-chain input');

autoChains.forEach(input => {
  input.addEventListener('change', (e) => {
    const parent = e.target.closest('.cyber-switch-container');
    if (e.target.checked) {
      parent.classList.add('active');
      if (!actkToggle.checked) {
        actkToggle.checked = true;
        actkToggle.closest('.cyber-switch-container').classList.add('active');
        log('Auto-mode acionado: ACTk Bypass ativado automaticamente.');
      }
    } else {
      parent.classList.remove('active');
    }
  });
});

// Outros toggles
document.querySelectorAll('.cyber-switch-container:not(.auto-chain) input').forEach(input => {
  input.addEventListener('change', (e) => {
    const parent = e.target.closest('.cyber-switch-container');
    if (e.target.checked) parent.classList.add('active');
    else parent.classList.remove('active');
  });
});

// Evolve Reconcile Mock
const evolveToggle = document.getElementById('evolveToggle');
evolveToggle.addEventListener('change', (e) => {
  if (e.target.checked) {
    log('Evolution Climb iniciado...');
    setTimeout(() => {
      if (evolveToggle.checked) {
        evolveToggle.checked = false;
        evolveToggle.closest('.cyber-switch-container').classList.remove('active');
        log('Evolution stop: Torment 3-9 atingido.');
      }
    }, 5000);
  }
});


// ============================================
// Stats Grid Override
// ============================================
const statBoxes = document.querySelectorAll('.stat-box');
const statMocks = document.querySelectorAll('.stat-val-mock');

// Simulate read tick
setInterval(() => {
  if (!state.isAttached) return;
  statMocks.forEach(span => {
    if(span.textContent === '--') {
      span.textContent = (Math.random() * 100).toFixed(1);
    }
  });
}, 1000);

statBoxes.forEach(box => {
  const btn = box.querySelector('.btn-apply');
  const input = box.querySelector('.stat-input');
  const current = box.querySelector('.stat-current');

  btn.addEventListener('click', () => {
    if (!state.isAttached) {
      log('Falha: Engine offline.');
      return;
    }
    
    if (!box.classList.contains('forced')) {
      const val = parseFloat(input.value);
      if (isNaN(val)) {
        log('Erro: Insira um valor numérico válido.');
        return;
      }
      box.classList.add('forced');
      btn.textContent = 'Forced ✓';
      current.textContent = val;
      log('Valor override injetado em memória.');
    } else {
      box.classList.remove('forced');
      btn.textContent = 'Override';
      log('Override removido.');
    }
  });
});
