// The 3D renderer of the Simplified Universal Exporter's showroom.
//
// Adapted from the 3D preview of RM Designs' Steering Wheel Swapper v1.7.1
// (src/WheelSwap.Core/WheelPreview.cs, https://github.com/RMDesignsStudio/Steering-Wheel-Swapper),
// copyright RM Designs, licensed under the GNU General Public License v3.0.
// Changes: the scene arrives from the host as JSON plus one binary buffer it points into (instead of
// zlib + base64 in a <script> block), only the whole-model ("car") views are offered, the studio, floor,
// camera and zoom scale with the model's size, the toolbar follows the studio colour (grey by
// default), and readiness is reported to the host.
// Distributed under the GNU GPL v3.0 as part of Simplified Suite - see LICENSE and NOTICE.md.
function WheelPreviewInit(root,data,bin){
if(!root)return;
const canvas=root.querySelector('.pv-canvas'),msg=root.querySelector('.pv-msg');
const gl=canvas.getContext('webgl2',{antialias:true});
if(!gl){msg.textContent='3D preview needs WebGL2 (any current browser).';return;}
const VS=`#version 300 es
in vec3 aPos;in vec4 aNrm;in vec4 aTan;in vec4 aUv;
uniform mat4 uVP,uModel,uLightVP;out vec3 vPos;out vec3 vN;out vec4 vT;out vec4 vUv;out float vAo;out vec4 vSh;
void main(){vec4 w=uModel*vec4(aPos,1.);mat3 m=mat3(uModel);vPos=w.xyz;vN=m*aNrm.xyz;vT=vec4(m*aTan.xyz,aTan.w);vUv=aUv;vAo=aNrm.w;vSh=uLightVP*w;gl_Position=uVP*w;}`;
// shadow pass: depth from the key light
const SVS=`#version 300 es
in vec3 aPos;uniform mat4 uLightVP,uModel;void main(){gl_Position=uLightVP*uModel*vec4(aPos,1.);}`;
const SFS=`#version 300 es
precision mediump float;out vec4 o;void main(){o=vec4(1.);}`;
const FS=`#version 300 es
precision highp float;
in vec3 vPos;in vec3 vN;in vec4 vT;in vec4 vUv;in float vAo;in vec4 vSh;uniform highp sampler2DShadow uShadow;uniform int uHasShadow;
uniform sampler2D uD,uN,uS,uL,uC,uF,uLS;uniform int uKind,uStudio;uniform vec3 uRoom,uGlow,uSkyLo,uSkyHi,uFloor;uniform vec4 uHas,uHas2,uP0,uP1,uP2,uP3,uP4,uP5;uniform vec3 uEye;
out vec4 o;
const float PI=3.14159265;
// car mode: a neutral dark showroom (as in the game's car viewer, without its coloured wall) lit by an overhead
// softbox and low strip lights, so metallic paint only shows its colour where it catches a light
vec3 studio(vec3 r,float rough){float w=mix(.02,.55,rough);vec3 c=uRoom;
  c+=vec3(2.4)*smoothstep(.8-w,.8+w*.5,r.y);
  c+=vec3(1.)*(1.-smoothstep(0.,.05+w,abs(r.y-.2)))*(.55+.45*abs(r.x));
  c+=uGlow*smoothstep(.1-w,.7,-r.x)*(1.-smoothstep(0.,.45+w,abs(r.y-.25)));
  return mix(c,uRoom+vec3(.14),rough*rough*.5);}
vec3 env(vec3 r,float rough){if(uStudio==1)return studio(r,rough);float t=clamp(r.y*.5+.5,0.,1.);vec3 sky=mix(uSkyLo,uSkyHi,pow(t,1.6));
  float sun=pow(max(dot(r,normalize(vec3(-.3,.75,-.6))),0.),mix(400.,8.,rough));return sky+vec3(1.,.97,.92)*sun*(1.-rough)*2.;}
float D(float nh,float a){float a2=a*a;float d=nh*nh*(a2-1.)+1.;return a2/(PI*d*d);}
float G(float nv,float nl,float a){float k=(a+1.)*(a+1.)/8.;return nv/(nv*(1.-k)+k)*nl/(nl*(1.-k)+k);}
vec3 F(float c,vec3 f0){return f0+(1.-f0)*pow(1.-c,5.);}
vec3 lin(vec3 c){return pow(c,vec3(2.2));}
vec2 strip(vec2 uv,ivec2 sz){return sz.y==0?uv:vec2(uv.x,fract(uv.y)*float(sz.x)/float(sz.y));}
vec3 nmap(sampler2D s,vec2 uv,vec3 n){vec3 t=vT.xyz-n*dot(n,vT.xyz);if(dot(t,t)<1e-8)return n;t=normalize(t);vec3 b=cross(n,t)*(vT.w<0.?-1.:1.);
  vec2 xy=texture(s,uv).rg*2.-1.;vec3 tn=vec3(xy,sqrt(max(0.,1.-dot(xy,xy))));return normalize(t*tn.x+b*tn.y+n*tn.z);}
// 5x5 PCF over the key light's shadow map (hardware-compared, bilinear), soft-edged
float shadowAt(){if(uHasShadow==0)return 1.;vec3 p=vSh.xyz/vSh.w*.5+.5;if(any(lessThan(p,vec3(0.)))||any(greaterThan(p,vec3(1.))))return 1.;
  vec2 t=1./vec2(textureSize(uShadow,0));float s=0.;for(int x=-2;x<=2;x++)for(int y=-2;y<=2;y++)s+=texture(uShadow,vec3(p.xy+vec2(x,y)*t,p.z-.0008));return s/25.;}
void main(){
  vec3 n=normalize(vN);if(!gl_FrontFacing)n=-n;vec3 gn=n;float sh0=shadowAt();
  // the studio's key is a large softbox: shadow on the car is partial (the floor below keeps the full shadow)
  float sh=mix(.35,1.,sh0);
  if(uKind==9){vec2 q=abs(vPos.xz-uP0.xy)-uP0.zw;float d=length(max(q,0.))+min(max(q.x,q.y),0.);
    float cont=1.-smoothstep(-.35*uP1.x,.35*uP1.x,d);float fade=1.-smoothstep(3.5*uP1.x,9.*uP1.x,length(vPos.xz-uP0.xy));
    o=vec4(mix(uFloor,uFloor*.08,clamp((1.-sh0)*.88+cont*.3,0.,1.)),fade);return;}
  vec2 uv0=vUv.xy,uv1=vUv.zw;
  vec3 alb=vec3(.3);float rough=.5,metal=0.,alpha=1.,coat=0.,coatR=.05,occ=1.;vec3 emit=vec3(0.);vec3 mc=vec3(-1.);
  if(uKind==1){vec2 t=uv0+uP1.xy;
    // paint_s: R = lacquer mask, G = roughness, B = metallic; paint_c = the metallic areas' reflection colour
    if(uHas.x>0.)alb=lin(texture(uD,t).rgb);
    coat=1.;
    if(uHas.z>0.){vec4 s=texture(uS,t);rough=clamp(s.g,.04,1.);metal=s.b;coat=s.r;}else rough=.35;
    if(uHas2.x>0.)mc=lin(texture(uC,t).rgb)+uP5.rgb;
    if(uHas2.y>0.&&metal>0.)n=normalize(mix(n,nmap(uF,uv0*uP4.y*96.,n),uP4.x));   // flakes are microscopic: many repeats, so they average out with distance
    coatR=max(uP1.w,.03);}
  else if(uKind==2){vec2 t=uv0*uP1.z+uP0.zw;alb=vec3(.018);rough=.3;
    if(uHas.x>0.){vec4 p=texture(uD,t);rough=clamp(p.g,.05,1.);metal=p.b;}
    if(uHas.y>0.)n=nmap(uN,t,n);
    coat=uP2.y;coatR=max(uP1.w,.03);}
  else if(uKind==3){alb=vec3(.02);alpha=.25;rough=.03;if(uHas.x>0.){vec4 c=texture(uD,uv0);alb=lin(c.rgb);alpha=c.a;}}
  else if(uKind==4){alb=vec3(.006);rough=.08;emit=vec3(.004,.006,.01)*(1.+uP2.z*4.);}
  else if(uKind==5){alb=vec3(.8);metal=1.;rough=.04;}
  else if(uKind==6){alpha=0.;if(uHas.x>0.){vec4 c=texture(uD,uv1);alb=lin(c.rgb);alpha=c.a;}}
  else{vec2 t=uv0*uP0.xy,tD=t,tN=t,tS=t;
    // tread sheets stack strips as tall as the sheet is wide (8 on the colour sheet, 4 on the normal map); a new
    // tyre is the first strip of each
    if(uKind==7&&uP0.y<0.){tD=strip(uv0,textureSize(uD,0));tN=strip(uv0,textureSize(uN,0));tS=strip(uv0,textureSize(uS,0));}
    vec4 dc=uHas.x>0.?texture(uD,tD):vec4(0.);
    if(uHas.x>0.)alb=lin(dc.rgb);else if(uKind==7)alb=vec3(.035);
    // surface map: R = baked occlusion (rims: dark barrel and crevices), G = roughness, B = metallic
    if(uHas.z>0.){vec4 s=texture(uS,tS);rough=clamp(s.g,.04,1.);metal=s.b;if(uKind!=7)occ=mix(.2,1.,s.r);}
    if(uHas.y>0.)n=nmap(uN,tN,n);
    if(uKind==7){metal=0.;rough=clamp(rough,.42,.92);
      // sidewall: the colour sheet's alpha holds the moulded radial texture; the lettering mask wraps like the sidewall
      if(uP0.y>0.&&uHas.x>0.)rough=mix(rough,.5,dc.a*.6);
      if(uHas.w>0.){vec4 l=texture(uL,fract(uv0));float c=l.r*l.a;c=c<.04?0.:sqrt(c);alb=mix(alb,lin(uP3.rgb),c);rough=mix(rough,uP2.x,c);}}
    if(uKind==8)emit=alb*.12;}
  if(uHas.w>0.&&uKind!=3&&uKind!=6&&uKind!=7){vec4 l=texture(uL,uv1);float a=l.a;alb=mix(alb,lin(l.rgb),a);n=normalize(mix(n,gn,a));
    if(uHas2.z>0.){vec4 ls=texture(uLS,uv1);rough=mix(rough,clamp(ls.g,.04,1.),a);metal=mix(metal,ls.b,a);}else{rough=mix(rough,uP2.x,a);metal=mix(metal,0.,a);}
    if(mc.x>=0.)mc=mix(mc,alb,a);}
  if(alpha<.004)discard;
  float ao=(uP2.w>0.?clamp(vAo,0.,1.):1.)*occ;
  vec3 v=normalize(uEye-vPos);float nv=max(dot(n,v),1e-4),gv=max(dot(gn,v),1e-4);
  vec3 f0=mix(vec3(.04),mc.x>=0.?mc:alb,metal);vec3 col=vec3(0.);float a=rough*rough,ac=coatR*coatR;
  float Fcv=F(gv,vec3(.04)).x*coat;
  vec3 L[2];L[0]=normalize(vec3(-.35,.8,-.6));L[1]=normalize(vec3(.6,.25,.45));vec3 C[2];C[0]=vec3(3.2);C[1]=vec3(.9,.95,1.1);
  for(int i=0;i<2;i++){vec3 l=L[i];vec3 h=normalize(l+v);float nl=max(dot(n,l),0.);
    if(nl>0.){float nh=max(dot(n,h),0.);vec3 Fr=F(max(dot(h,v),0.),f0);vec3 spec=D(nh,a)*G(nv,nl,a)*Fr/(4.*nv*nl+1e-4);vec3 kd=(1.-Fr)*(1.-metal);
      col+=(kd*alb/PI+spec)*C[i]*nl*(1.-Fcv)*(i==0?sh:1.)*mix(1.,occ,.6);}
    float gnl=max(dot(gn,l),0.);if(coat>0.&&gnl>0.){float gh=max(dot(gn,h),0.);float Fc=F(max(dot(h,v),0.),vec3(.04)).x;
      col+=coat*D(gh,ac)*G(gv,gnl,ac)*Fc/(4.*gv*gnl+1e-4)*C[i]*gnl*(i==0?sh:1.);}}
  // in the studio most ambient light comes from the overhead softbox, so shadowed areas get less of it
  if(uStudio==1)ao*=mix(.7,1.,sh0);
  vec3 r=reflect(-v,n);vec3 Fe=F(nv,f0);
  col+=((1.-Fe)*(1.-metal)*alb*env(n,1.)*.55+Fe*env(r,rough)*(1.-rough*.7)*.6*uP3.w)*ao*(1.-Fcv)+Fcv*env(reflect(-v,gn),coatR)*.65+emit;
  if(uKind==3)alpha=max(alpha,Fe.x*.9);
  if(uKind==7)col+=vec3(.035)*pow(1.-nv,3.)*env(n,.8)*ao;   // rubber's soft grazing sheen
  col=col*(2.51*col+.03)/(col*(2.43*col+.59)+.14);
  o=vec4(pow(clamp(col,0.,1.),vec3(1./2.2)),alpha);}`;
function sh(t,s){const x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw gl.getShaderInfoLog(x);return x;}
const ATTR=['aPos','aNrm','aTan','aUv'];
function program(vs,fs){const p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,vs));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,fs));
  ATTR.forEach((a,i)=>gl.bindAttribLocation(p,i,a));gl.linkProgram(p);return p;}
const prog=program(VS,FS),sprog=program(SVS,SFS);
if(!gl.getProgramParameter(prog,gl.LINK_STATUS)){msg.textContent='3D preview: '+gl.getProgramInfoLog(prog);return;}
const U={};const u=n=>U[n]||(U[n]=gl.getUniformLocation(prog,n));
function bytes(b){const s=atob(b),a=new Uint8Array(s.length);for(let i=0;i<s.length;i++)a[i]=s.charCodeAt(i);return a;}
// every blob is zlib + base64: inflate them all once, before the first frame
// the host sends one binary buffer; every blob is an [offset, length] slice of it
        async function inflate(r){return bin.subarray(r[0],r[0]+r[1]);}
// textures: block-compressed levels as shipped (BC1/2/3 s3tc, BC4/5 rgtc, BC7 bptc) or plain RGBA
const ext={s3tc:gl.getExtension('WEBGL_compressed_texture_s3tc'),rgtc:gl.getExtension('EXT_texture_compression_rgtc'),bptc:gl.getExtension('EXT_texture_compression_bptc')};
const aniso=gl.getExtension('EXT_texture_filter_anisotropic');
const FMT={bc1:['s3tc',0x83F1],bc2:['s3tc',0x83F2],bc3:['s3tc',0x83F3],bc4:['rgtc',0x8DBB],bc5:['rgtc',0x8DBD],bc7:['bptc',0x8E8C]};
const missing=new Set(),texs=[];
function texture(i){if(i==null||i<0)return null;if(texs[i]!==undefined)return texs[i];const t=data.tex[i];if(t.f==='none')return texs[i]=null;
  if(t.f!=='rgba'&&!(FMT[t.f]&&ext[FMT[t.f][0]])){missing.add(t.f);return texs[i]=null;}
  const x=gl.createTexture();gl.activeTexture(gl.TEXTURE8);gl.bindTexture(gl.TEXTURE_2D,x);   // a scratch unit: never one a draw samples
  if(t.f==='rgba'){gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA8,t.w,t.h,0,gl.RGBA,gl.UNSIGNED_BYTE,t.l[0]);gl.generateMipmap(gl.TEXTURE_2D);}
  else{t.l.forEach((l,j)=>gl.compressedTexImage2D(gl.TEXTURE_2D,j,FMT[t.f][1],Math.max(1,t.w>>j),Math.max(1,t.h>>j),0,l));
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAX_LEVEL,t.l.length-1);}
  t.l=null;return texs[i]=x;}
function sampler(wrap){const s=gl.createSampler();gl.samplerParameteri(s,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_LINEAR);gl.samplerParameteri(s,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
  gl.samplerParameteri(s,gl.TEXTURE_WRAP_S,wrap);gl.samplerParameteri(s,gl.TEXTURE_WRAP_T,wrap);if(aniso)gl.samplerParameterf(s,aniso.TEXTURE_MAX_ANISOTROPY_EXT,8);return s;}
const repeat=sampler(gl.REPEAT),clamp=sampler(gl.CLAMP_TO_EDGE);
// geometry: float32 positions, half normal/tangent/uv, 16/32-bit indices; uploaded on first draw
function vao(g){if(g.vao)return g.vao;const v=gl.createVertexArray();gl.bindVertexArray(v);
  [['aPos','pos',3,gl.FLOAT],['aNrm','nrm',4,gl.HALF_FLOAT],['aTan','tan',4,gl.HALF_FLOAT],['aUv','uv',4,gl.HALF_FLOAT]].forEach(([a,k,n,ty])=>{
    const loc=gl.getAttribLocation(prog,a);if(loc<0)return;const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);
    gl.bufferData(gl.ARRAY_BUFFER,g[k],gl.STATIC_DRAW);gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,n,ty,false,0,0);g[k]=null;});
  const ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,g.idx,gl.STATIC_DRAW);g.idx=null;
  gl.bindVertexArray(null);return g.vao=v;}
let groundV=null;
function groundVao(){if(groundV)return groundV;const v=gl.createVertexArray();gl.bindVertexArray(v);const S=40;
  const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-S,0,-S,S,0,-S,S,0,S,-S,0,S]),gl.STATIC_DRAW);
  const loc=gl.getAttribLocation(prog,'aPos');gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,3,gl.FLOAT,false,0,0);
  const ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,new Uint16Array([0,1,2,0,2,3]),gl.STATIC_DRAW);
  gl.bindVertexArray(null);return groundV=v;}
function material(m){const t=m.t,has=[t.d,t.n,t.s,t.l].map(i=>texture(i)?1:0),has2=[t.c,t.f,t.ls,-1].map(i=>texture(i)?1:0);
  gl.uniform4fv(u('uHas2'),has2);gl.uniform4fv(u('uP4'),m.p.slice(16,20));gl.uniform4fv(u('uP5'),m.p.slice(20,24));
  [['uD',t.d,repeat],['uN',t.n,repeat],['uS',t.s,repeat],['uL',t.l,clamp],['uC',t.c,repeat],['uF',t.f,repeat],['uLS',t.ls,clamp]].forEach(([n,i,s],k)=>{gl.activeTexture(gl.TEXTURE0+k);gl.bindTexture(gl.TEXTURE_2D,texture(i));gl.bindSampler(k,s);gl.uniform1i(u(n),k);});
  gl.uniform1i(u('uKind'),m.k);gl.uniform4fv(u('uHas'),has);gl.uniform4fv(u('uP0'),m.p.slice(0,4));gl.uniform4fv(u('uP1'),m.p.slice(4,8));gl.uniform4fv(u('uP2'),m.p.slice(8,12));gl.uniform4fv(u('uP3'),m.p.slice(12,16));}
// camera: wheel space (x across, y up, z along the car; the driver sits at -z) or car space (front = +z)
const I4=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
const VIEWS={wheel:[['driver',"Driver's view",{yaw:0,pitch:.2,dist:.5,fov:42,space:'wheel'}],['front','Front',{yaw:0,pitch:0,dist:.5,fov:36,space:'wheel'}],
    ['side','Side',{yaw:Math.PI/2,pitch:.05,dist:.45,fov:36,space:'wheel'}],['three','3/4',{yaw:.75,pitch:.35,dist:.5,fov:36,space:'wheel'}]],
  car:[['front','Front',{yaw:Math.PI,pitch:.1,dist:6.6,fov:30,space:'car'}],
    ['side','Side',{yaw:Math.PI/2,pitch:.05,dist:8.4,fov:30,space:'car'}],['three','3/4',{yaw:2.35,pitch:.28,dist:7.6,fov:30,space:'car'}],
    ['top','Top',{yaw:Math.PI,pitch:1.45,dist:9,fov:30,space:'car'}]]};
const cars=data.cars;let car=0,side='after',mode='car',cam=null,target=[0,0,0];
        const B0=cars[0].box,SCALE=Math.max(.05,Math.hypot(B0[3]-B0[0],B0[4]-B0[1],B0[5]-B0[2])/5.6);
function home(view){const c=cars[car];cam={...view};
          // fit the model's own height and width (RM's distances were set for a car's long, low shape;
          // their ratios between views are kept, 3/4 = 7.6)
          if(view.space==='car'){const b=c.box,H=b[4]-b[1],W=Math.max(b[3]-b[0],b[5]-b[2]);
            cam.dist=Math.max(H*.9,W*.42)/Math.tan(cam.fov*Math.PI/360)*view.dist/7.6;}
  if(view.space==='car'){const b=c.box;target=[(b[0]+b[3])/2,(b[1]+b[4])/2,(b[2]+b[5])/2];}else target=(view.t||[0,0,-.03]).slice();}
function persp(f,a,n,fa){const t=1/Math.tan(f/2);return [t/a,0,0,0,0,t,0,0,0,0,(fa+n)/(n-fa),-1,0,0,2*fa*n/(n-fa),0];}
function look(e,c,up){const z=norm(sub(e,c)),x=norm(cross(up,z)),y=cross(z,x);return [x[0],y[0],z[0],0,x[1],y[1],z[1],0,x[2],y[2],z[2],0,-dot(x,e),-dot(y,e),-dot(z,e),1];}
function mul(a,b){const r=new Array(16).fill(0);for(let i=0;i<4;i++)for(let j=0;j<4;j++)for(let k=0;k<4;k++)r[j*4+i]+=a[k*4+i]*b[j*4+k];return r;}
const sub=(a,b)=>[a[0]-b[0],a[1]-b[1],a[2]-b[2]],dot=(a,b)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2],cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],norm=a=>{const l=Math.hypot(...a);return [a[0]/l,a[1]/l,a[2]/l];};
function eye(){return [target[0]-Math.sin(cam.yaw)*Math.cos(cam.pitch)*cam.dist,target[1]+Math.sin(cam.pitch)*cam.dist,target[2]-Math.cos(cam.yaw)*Math.cos(cam.pitch)*cam.dist];}
function drawList(list,blend){for(const g of list){const m=data.mats[g.m];if(!!m.blend!==blend)continue;const x=data.geo[g.g];material(m);gl.bindVertexArray(vao(x));
  gl.drawElements(gl.TRIANGLES,x.count,x.w32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT,0);}}
let pending=false;
// shadow map: the key light's depth, re-rendered only when what is shown changes (the light is fixed in the scene)
const SM=4096,shadowTex=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,shadowTex);
gl.texStorage2D(gl.TEXTURE_2D,1,gl.DEPTH_COMPONENT24,SM,SM);
gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_COMPARE_MODE,gl.COMPARE_REF_TO_TEXTURE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_COMPARE_FUNC,gl.LEQUAL);
const shadowFb=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,shadowFb);
gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.DEPTH_ATTACHMENT,gl.TEXTURE_2D,shadowTex,0);gl.drawBuffers([gl.NONE]);gl.readBuffer(gl.NONE);
const shadowOk=gl.checkFramebufferStatus(gl.FRAMEBUFFER)===gl.FRAMEBUFFER_COMPLETE;gl.bindFramebuffer(gl.FRAMEBUFFER,null);
const KEY=norm([-.35,.8,-.6]);let shadowKey='',lightVP=I4;
function ortho(r,n,f){return [1/r,0,0,0,0,1/r,0,0,0,0,-2/(f-n),0,0,0,-(f+n)/(f-n),1];}
function xf(m,p){return [m[0]*p[0]+m[4]*p[1]+m[8]*p[2]+m[12],m[1]*p[0]+m[5]*p[1]+m[9]*p[2]+m[13],m[2]*p[0]+m[6]*p[1]+m[10]*p[2]+m[14]];}
function updateShadow(list,model,b,key){
  if(!shadowOk||key===shadowKey)return;shadowKey=key;
  const c=xf(model,[(b[0]+b[3])/2,(b[1]+b[4])/2,(b[2]+b[5])/2]),r=Math.hypot(b[3]-b[0],b[4]-b[1],b[5]-b[2])*.62;
  const up=Math.abs(KEY[1])>.95?[1,0,0]:[0,1,0];
  lightVP=mul(ortho(r,.01,r*4),look([c[0]+KEY[0]*r*2,c[1]+KEY[1]*r*2,c[2]+KEY[2]*r*2],c,up));
  gl.bindFramebuffer(gl.FRAMEBUFFER,shadowFb);gl.viewport(0,0,SM,SM);gl.clear(gl.DEPTH_BUFFER_BIT);
  gl.useProgram(sprog);gl.uniformMatrix4fv(gl.getUniformLocation(sprog,'uLightVP'),false,lightVP);gl.uniformMatrix4fv(gl.getUniformLocation(sprog,'uModel'),false,model);
  gl.enable(gl.DEPTH_TEST);gl.depthMask(true);gl.disable(gl.BLEND);gl.enable(gl.POLYGON_OFFSET_FILL);gl.polygonOffset(1.5,3);
  for(const g of list){if(data.mats[g.m].blend)continue;const x=data.geo[g.g];gl.bindVertexArray(vao(x));gl.drawElements(gl.TRIANGLES,x.count,x.w32?gl.UNSIGNED_INT:gl.UNSIGNED_SHORT,0);}
  gl.disable(gl.POLYGON_OFFSET_FILL);gl.bindVertexArray(null);gl.bindFramebuffer(gl.FRAMEBUFFER,null);}
// studio colours (RM Designs palette): purple (#1C0040 / #4300AD glow), grey (#111111), white (#F2F2F2)
const THEMES={purple:{bg:[.07,.05,.12],room:[.022,.021,.026],glow:[.07,.015,.32],sky:[[.10,.09,.12],[.62,.62,.68]],floor:[.17,.155,.21],text:'#bfb2ea'},
  grey:{bg:[.067,.067,.067],room:[.022,.022,.022],glow:[0,0,0],sky:[[.10,.10,.10],[.64,.64,.64]],floor:[.17,.17,.17],text:'#bdbdbd'},
  white:{bg:[.949,.949,.949],room:[.42,.42,.42],glow:[0,0,0],sky:[[.42,.42,.42],[.95,.95,.95]],floor:[.86,.86,.86],text:'#4300AD'}};
let theme='grey';try{const t=localStorage.getItem('pv-theme');if(THEMES[t])theme=t;}catch(e){}
function setTheme(t){theme=t;root.dataset.theme=t;root.querySelectorAll('[data-theme]').forEach(x=>x.classList.toggle('on',x.dataset.theme===t));
  canvas.style.background='rgb('+THEMES[t].bg.map(v=>Math.round(v*255)).join(',')+')';try{localStorage.setItem('pv-theme',t);}catch(e){}draw();}
root.querySelectorAll('[data-theme]').forEach(b=>b.onclick=()=>setTheme(b.dataset.theme));
let ready=false;
function draw(){if(!ready||pending)return;pending=true;requestAnimationFrame(()=>{pending=false;render();});}
function render(){
  const dpr=Math.min(window.devicePixelRatio||1,2),w=canvas.clientWidth*dpr|0,h=canvas.clientHeight*dpr|0;
  if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;}
  gl.viewport(0,0,w,h);const th=THEMES[theme];gl.clearColor(th.bg[0],th.bg[1],th.bg[2],1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);gl.disable(gl.CULL_FACE);
  const c=cars[car],e=eye(),inCar=mode==='car'&&!!c.body,wheelSpace=cam.space==='wheel';
  const model=wheelSpace?c.toWheel:I4,list=(inCar?c.body:[]).concat(c[side]);
  updateShadow(list,model,inCar?c.box:(c.wbox||c.box),[car,side,mode,cam.space].join('|'));
  gl.viewport(0,0,w,h);
  gl.useProgram(prog);gl.uniform1i(u('uStudio'),inCar?1:0);
  gl.uniform3fv(u('uRoom'),th.room);gl.uniform3fv(u('uGlow'),th.glow);gl.uniform3fv(u('uSkyLo'),th.sky[0]);gl.uniform3fv(u('uSkyHi'),th.sky[1]);gl.uniform3fv(u('uFloor'),th.floor);
  msg.style.color=th.text;
  gl.uniformMatrix4fv(u('uLightVP'),false,lightVP);gl.uniform1i(u('uHasShadow'),shadowOk?1:0);
  gl.activeTexture(gl.TEXTURE7);gl.bindTexture(gl.TEXTURE_2D,shadowTex);gl.bindSampler(7,null);gl.uniform1i(u('uShadow'),7);
  const near=wheelSpace?.01:.05*SCALE,far=wheelSpace?(inCar?40:10):120*SCALE;
  gl.uniformMatrix4fv(u('uVP'),false,mul(persp(cam.fov*Math.PI/180,w/h,near,far),look(e,target,[0,1,0])));gl.uniform3fv(u('uEye'),e);
  gl.uniformMatrix4fv(u('uModel'),false,model);
  gl.disable(gl.BLEND);gl.depthMask(true);drawList(list,false);
  gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);gl.depthMask(false);
  if(inCar&&!wheelSpace){const b=c.box;gl.uniform1i(u('uKind'),9);
    gl.uniformMatrix4fv(u('uModel'),false,[SCALE,0,0,0,0,1,0,0,0,0,SCALE,0,0,b[1]+.004*SCALE,0,1]);gl.uniform4fv(u('uP1'),[SCALE,0,0,0]);
    gl.uniform4fv(u('uP0'),[(b[0]+b[3])/2,(b[2]+b[5])/2,(b[3]-b[0])/2,(b[5]-b[2])/2]);gl.bindVertexArray(groundVao());gl.drawElements(gl.TRIANGLES,6,gl.UNSIGNED_SHORT,0);
    gl.uniformMatrix4fv(u('uModel'),false,model);}
  drawList(list,true);
  gl.depthMask(true);gl.disable(gl.BLEND);gl.bindVertexArray(null);
  msg.textContent=missing.size?'this view cannot show '+[...missing].join('/')+' textures':'';}
// controls
const viewBar=root.querySelector('.pv-views');
function setMode(m){mode=m;if(!ready){root.querySelectorAll('[data-mode]').forEach(x=>x.classList.toggle('on',x.dataset.mode===m));return;}root.querySelectorAll('[data-mode]').forEach(x=>x.classList.toggle('on',x.dataset.mode===m));
  const vs=VIEWS[m],first=m==='car'?2:0;viewBar.innerHTML='';
  vs.forEach(([k,label,v],i)=>{const b=document.createElement('button');b.textContent=label;b.dataset.view=k;if(i===first)b.classList.add('on');
    b.onclick=()=>{home(v);viewBar.querySelectorAll('button').forEach(x=>x.classList.toggle('on',x===b));draw();};viewBar.appendChild(b);});
  home(vs[first][2]);draw();}
let drag=null;
canvas.addEventListener('contextmenu',e=>e.preventDefault());
canvas.addEventListener('dblclick',()=>{const b=viewBar.querySelector('button.on');if(b&&ready)b.click();});
canvas.addEventListener('pointerdown',e=>{drag={x:e.clientX,y:e.clientY,b:e.button};canvas.setPointerCapture(e.pointerId);});
canvas.addEventListener('pointerup',()=>drag=null);
canvas.addEventListener('pointermove',e=>{if(!drag||!ready)return;const dx=e.clientX-drag.x,dy=e.clientY-drag.y;drag.x=e.clientX;drag.y=e.clientY;
  if(drag.b===2||e.shiftKey){const k=cam.dist*.0016;target[0]+=dx*k*Math.cos(cam.yaw);target[2]-=dx*k*Math.sin(cam.yaw);target[1]+=dy*k;}
  else{cam.yaw-=dx*.008;cam.pitch=Math.max(-1.5,Math.min(1.5,cam.pitch+dy*.008));}draw();});
canvas.addEventListener('wheel',e=>{e.preventDefault();if(!ready)return;const [lo,hi]=cam.space==='car'?[.2*SCALE,30*SCALE]:[.12,3];cam.dist=Math.max(lo,Math.min(hi,cam.dist*Math.exp(e.deltaY*.0012)));draw();},{passive:false});
root.querySelectorAll('[data-mode]').forEach(b=>b.onclick=()=>setMode(b.dataset.mode));
root.querySelectorAll('[data-side]').forEach(b=>b.onclick=()=>{side=b.dataset.side;root.querySelectorAll('[data-side]').forEach(x=>x.classList.toggle('on',x===b));draw();});
const sel=root.querySelector('.pv-car');if(sel)sel.onchange=()=>{car=+sel.value;draw();};
msg.textContent='Loading 3D data…';
Promise.all([...data.tex.map(async t=>{t.l=await Promise.all(t.l.map(inflate));}),
  ...data.geo.map(async g=>{for(const k of ['pos','nrm','tan','uv','idx'])g[k]=await inflate(g[k]);})])
  .then(()=>{ready=true;new ResizeObserver(draw).observe(canvas);setTheme(theme);setMode(mode);
    const dbg=gl.getExtension('WEBGL_debug_renderer_info');
    window.chrome?.webview?.postMessage({ready:performance.now(),ext:{s3tc:!!ext.s3tc,rgtc:!!ext.rgtc,bptc:!!ext.bptc},
      renderer:dbg?gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL):'',missing:[...missing]});})
  .catch(e=>{msg.textContent='3D preview could not load its data: '+e;});}
